using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Expands the four-wheel workplace in older factory scenes and saved authoring drafts.</summary>
public static class WheelProcessMigration
{
    static readonly PartType[] Wheels = {
        PartType.Wheel_FrontRight_41, PartType.Wheel_BehindRight_42,
        PartType.Wheel_FrontLeft_43, PartType.Wheel_BehindLeft_44
    };
    static readonly string[] Names = { "오른쪽 앞바퀴", "오른쪽 뒷바퀴", "왼쪽 앞바퀴", "왼쪽 뒷바퀴" };

    static string ProcessId(string original, int i) => (original ?? "wheel") + "/" + Wheels[i];
    static string WorkerId(string original, int i) => i % 2 == 0 ? original : original + "/" + Wheels[i];
    static T Copy<T>(T value) => JsonUtility.FromJson<T>(JsonUtility.ToJson(value));
    static bool IsTeam(ProcessLayoutRecord record) =>
        record.stocks.Count == Wheels.Length && Wheels.All(w => record.stocks.Exists(s => s.part == w));

    public static bool IsWheel(PartType type) => Array.IndexOf(Wheels, type) >= 0;

    public static ProcessAuthoringProfile ForRuntime(ProcessAuthoringProfile source)
    {
        if (source == null || !source.processes.Exists(IsTeam)) return source;
        var copy = Object.Instantiate(source);
        copy.hideFlags = HideFlags.DontSave;
        ExpandProfile(copy);
        return copy;
    }

    // Call only on the runtime copy or the workbench draft. The source asset changes on explicit Save.
    public static void ExpandProfile(ProcessAuthoringProfile profile)
    {
        if (profile == null) return;
        int index = profile.processes.FindIndex(IsTeam);
        if (index < 0) return;
        var team = profile.processes[index];
        var workers = team.actors.Where(a => a.kind == ProcessActorKind.Worker).ToArray();
        profile.processes.RemoveAt(index);
        for (int i = 0; i < Wheels.Length; i++)
        {
            var record = Copy(team);
            record.id = ProcessId(team.id, i);
            record.displayName = Names[i];
            record.hasHardware &= i == 0;
            string worker = workers[i / 2].id;
            record.actors.RemoveAll(a => a.kind == ProcessActorKind.Worker ? a.id != worker : a.part != Wheels[i]);
            var actor = record.FindActor(worker);
            actor.id = WorkerId(worker, i);
            record.stocks.RemoveAll(s => s.part != Wheels[i]);
            if (i >= 2)
            {
                record.hiringPostSide = -team.hiringPostSide;
                record.hiringPost.position.x = -team.hiringPost.position.x;
            }
            profile.processes.Insert(index + i, record);
            foreach (var vehicle in profile.vehicles)
            {
                foreach (var old in vehicle.overrides.Where(v => v.processId == team.id).ToArray())
                {
                    if (old.part != PartType.None && old.part != Wheels[i]) continue;
                    bool matchingActor = old.actorId == worker || record.FindActor(old.actorId) != null || string.IsNullOrEmpty(old.actorId);
                    if (!matchingActor && !old.overrideStop) continue;
                    var item = Copy(old);
                    item.processId = record.id;
                    if (!matchingActor) { item.actorId = string.Empty; item.overridePaths = item.overrideWait = item.overrideWork = false; }
                    if (old.actorId == worker) item.actorId = actor.id;
                    vehicle.overrides.Add(item);
                }
            }
        }
        foreach (var vehicle in profile.vehicles) vehicle.overrides.RemoveAll(v => v.processId == team.id);
    }

    public static void ExpandScene(ProductionProgression owner, ProcessAuthoringPreview preview)
    {
        if (owner == null || owner.processes == null) return;
        int index = Array.FindIndex(owner.processes, p => p?.parts != null && p.parts.Length == Wheels.Length && Wheels.All(w => Array.IndexOf(p.parts, w) >= 0));
        if (index < 0) return;
        var team = owner.processes[index];
        var entries = owner.processes.ToList();
        var groups = preview != null ? preview.processRoots.ToList() : null;
        var originalGroup = preview != null ? groups[index].transform : owner.transform;
        var station = team.humanStation;
        var stacks = station.partStacks;
        var sourceWorkers = team.workers;
        var extraWorkers = sourceWorkers.Select(w => Object.Instantiate(w, w.transform.parent, true)).ToArray();
        Vector3 originalPost = team.hiringPost != null ? team.hiringPost.position : Vector3.zero;
        var oldFrame = ProcessAuthoringLayout.Frame(owner, team);
        entries.RemoveAt(index);
        groups?.RemoveAt(index);
        // Keep the existing four stocks and arms. Clone only the extra person and placard for each new job.
        if (team.wheel != null) team.wheel.enabled = false;
        for (int i = 0; i < Wheels.Length; i++)
        {
            var part = Wheels[i];
            float gate = team.gateProgress;
            if (preview == null && index > 0 && index + 1 < owner.processes.Length)
                gate = Mathf.Lerp(owner.processes[index - 1].gateProgress, owner.processes[index + 1].gateProgress, (i + 1f) / (Wheels.Length + 1f));
            var frame = ProcessAuthoringLayout.FrameAt(owner.mainSpline, gate);
            var group = Child(owner.transform, "Process_" + part);
            group.gameObject.SetActive(false);
            group.position = oldFrame.position;
            var people = Child(group, "Workers");
            var arms = Child(group, "RobotArms");
            var storage = preview != null ? Child(group, "Inventory") : people;
            var person = sourceWorkers[i / 2];
            if (i % 2 == 1)
            {
                person = extraWorkers[i / 2];
                person.transform.SetParent(people, true);
                var key = person.GetComponent<ProcessAuthoringIdentity>();
                if (key != null) key.id = WorkerId(key.id, i);
            }
            else person.transform.SetParent(people, true);
            var stack = Array.Find(stacks, s => s != null && s.partType == part);
            var arm = Array.Find(team.robots, r => r != null && r.targetPartType == part);
            stack.transform.SetParent(storage, true);
            arm.transform.SetParent(arms, true);
            var human = people.gameObject.AddComponent<WorkerStation>();
            human.enabled = false;
            human.flowThrough = station.flowThrough;
            human.leadDistance = station.leadDistance;
            human.workZoneMeters = station.workZoneMeters;
            human.mainLineSpline = owner.mainSpline;
            human.autoProgressFromPosition = false;
            human.gateProgress = gate;
            human.workers = new[] { person };
            human.partStacks = new[] { stack };
            human.targetPartTypes = new[] { part };
            human.pilePos = stack.transform;
            var entry = new ProductionProgression.ProcessEntry {
                displayName = Names[i], authoringId = ProcessId(team.authoringId, i), row = team.row, parts = human.targetPartTypes,
                humanStation = human, workers = human.workers, robots = new[] { arm }, humanRoot = people.gameObject,
                gateProgress = gate, hired = team.hired, automated = team.automated, workerLevel = team.workerLevel,
                robotDecor = Array.Empty<GameObject>()
            };
            if (team.hiringPost != null)
            {
                var postParent = preview != null ? group : team.hiringPost.parent;
                entry.hiringPost = i == 0 ? team.hiringPost : Object.Instantiate(team.hiringPost, postParent, true);
                if (preview != null) entry.hiringPost.SetParent(group, true);
            }
            int robotIndex = Array.IndexOf(team.robots, arm);
            if (team.robotDecor != null && robotIndex < team.robotDecor.Length && team.robotDecor[robotIndex] != null)
            {
                var decor = team.robotDecor[robotIndex];
                decor.transform.position += frame.position - oldFrame.position;
                entry.robotDecor = new[] { decor };
            }
            if (i == 0 && team.wheel != null && team.wheel.liftPlatform != null)
            {
                entry.wheelPlatform = team.wheel.liftPlatform;
                entry.wheelPlatform.SetParent(group, true);
            }
            entries.Insert(index + i, entry);
            groups?.Insert(index + i, group.gameObject);
            if (preview == null)
            {
                // A scene with no layout profile still has independent positions and correctly stocked stations.
                group.position = frame.position;
                if (entry.hiringPost != null) entry.hiringPost.position = originalPost + frame.position - oldFrame.position;
                group.gameObject.SetActive(true);
            }
        }
        station.partStacks = Array.Empty<PartStack>();
        station.workers = Array.Empty<Worker>();
        station.enabled = false;
        team.humanRoot.SetActive(false);
        if (team.wheel != null)
        {
            team.wheel.humanPartStacks = Array.Empty<PartStack>();
            team.wheel.gameObject.SetActive(false);
        }
        owner.processes = entries.ToArray();
        if (preview != null)
        {
            originalGroup.gameObject.SetActive(false);
            preview.processRoots = groups.ToArray();
            if (preview.selectedProcess > index) preview.selectedProcess += Wheels.Length - 1;
            for (int i = index; i < index + Wheels.Length; i++)
            {
                var group = preview.processRoots[i];
                group.transform.Find("RobotArms").gameObject.SetActive(preview.robotMode);
                owner.processes[i].humanRoot.SetActive(!preview.robotMode);
                group.SetActive(i == preview.selectedProcess);
            }
        }
        for (int i = index; i < owner.processes.Length; i++)
        {
            var post = owner.processes[i].hiringPost;
            if (post == null) continue;
            if (preview == null)
            {
                post.name = $"Post_{i:D2}";
                post.SetSiblingIndex(i);
            }
            var label = post.Find("Index")?.GetComponent<TextMesh>();
            if (label != null) label.text = (i + 1).ToString("D2");
        }
    }

    static Transform Child(Transform parent, string name)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        return child;
    }
}
