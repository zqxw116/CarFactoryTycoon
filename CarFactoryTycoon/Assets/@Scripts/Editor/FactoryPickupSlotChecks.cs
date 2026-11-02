#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEngine;

/// <summary>Deterministic slot checks; uses synthetic pooled displays, no scene or prefab changes.</summary>
public static class FactoryPickupSlotChecks
{
    public static string Run()
    {
        GameObject fixture = new GameObject("PickupSlotChecks");
        GameObject owner = new GameObject("Owner");
        GameObject other = new GameObject("OtherOwner");
        GameObject hand = new GameObject("Hand");
        try
        {
            PartStack stack = fixture.AddComponent<PartStack>();
            stack.enabled = false;
            stack.capacity = 3;
            stack.autoFitCollider = false;
            GameObject[] pool = new GameObject[3];
            for (int i = 0; i < pool.Length; i++)
            {
                pool[i] = new GameObject("Display" + i);
                pool[i].transform.SetParent(fixture.transform, false);
            }
            Set(stack, "items", pool);
            Set(stack, "builtType", stack.partType);
            Set(stack, "builtCapacity", 3);
            Set(stack, "count", 3);
            Set(stack, "fillOrder", new[] { 0, 1, 2 });
            Set(stack, "rankOfSlot", new[] { 0, 1, 2 });
            Vector3 slot = new Vector3(5, 1, 2);
            Quaternion rotation = Quaternion.Euler(0, 90, 0);
            stack.ConfigureReadySlot(slot, rotation);
            hand.transform.position = slot;
            Require(stack.HasReadyPart && stack.Count == 3, "initial total includes staged part");
            Require((pool[2].transform.position - slot).sqrMagnitude < 0.00001f, "existing top display staged");
            Require(stack.TryReserveReady(owner) && !stack.TryReserveReady(other), "exclusive reservation");
            owner.SetActive(false);
            Require(stack.TryReserveReady(other), "disabled owner reservation released");
            owner.SetActive(true);
            Require(!stack.TryTakeReady(owner, hand.transform, out _, out _), "wrong owner cannot consume");
            Require(stack.TryTakeReady(other, hand.transform, out Vector3 taken, out Quaternion takenRotation), "physical pickup");
            Require(taken == slot && Quaternion.Angle(takenRotation, rotation) < 0.001f, "fixed root pose returned");
            Require(stack.Count == 2 && !stack.HasReadyPart && !pool[2].activeSelf, "pickup empties slot and decrements once");
            Require(!stack.TryTakeReady(other, hand.transform, out _, out _) && stack.Count == 2, "no duplicate pickup");
            float pickup = (float)Get(stack, "readyPickupTime");
            Tick(stack, pickup + 0.99f);
            Require(!stack.HasReadyPart, "no refill before clearance or timeout");
            hand.transform.position = slot + Vector3.right * 0.5f;
            Tick(stack, pickup + 0.1f);
            Require(stack.HasReadyPart && stack.Count == 2 && pool[1].transform.position == slot, "half metre clearance refills without stock change");
            hand.transform.position = slot;
            Require(stack.TryTakeReady(owner, hand.transform, out _, out _), "second pickup");
            UnityEngine.Object.DestroyImmediate(hand);
            pickup = (float)Get(stack, "readyPickupTime");
            Tick(stack, pickup + 1.001f);
            Require(stack.HasReadyPart && stack.Count == 1, "lost hand timeout refills");
            Require(stack.TryTakeReady(owner, null, out _, out _), "last pickup");
            Require(stack.Count == 0 && !stack.HasReadyPart, "zero stock no display");
            Require(stack.Add(2) == 2 && !stack.HasReadyPart, "supply during cooldown waits");
            Tick(stack, (float)Get(stack, "readyPickupTime") + 1.001f);
            Require(stack.HasReadyPart && stack.Count == 2, "supply reconnects");
            Require(stack.TryConsume() && stack.TryConsume() && !stack.TryConsume() && stack.Count == 0, "raw consumption compatibility");
            Tick(stack, Time.time + 3f);
            Require(!stack.HasReadyPart, "empty timeout creates no stock");
            Require(stack.Add(1) == 1 && stack.HasReadyPart, "supply after empty reconnects immediately");
            Require(pool[0] != null && pool[1] != null && pool[2] != null, "display pool identities retained");
            return "FactoryPickupSlotChecks PASS (reservation, pickup, clearance, timeout/lost hand, supply, raw API, pooled identity)";
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(fixture);
            UnityEngine.Object.DestroyImmediate(owner);
            UnityEngine.Object.DestroyImmediate(other);
            if (hand != null) UnityEngine.Object.DestroyImmediate(hand);
        }
    }

    public static string RunLegacy()
    {
        GameObject fixture = new GameObject("LegacyPickupChecks");
        try
        {
            PartStack stack = fixture.AddComponent<PartStack>();
            stack.enabled = false;
            stack.capacity = 2;
            stack.autoFitCollider = false;
            GameObject[] pool = new GameObject[2];
            for (int i = 0; i < pool.Length; i++)
            {
                pool[i] = new GameObject("Display" + i);
                pool[i].transform.SetParent(fixture.transform, false);
            }
            Set(stack, "items", pool);
            Set(stack, "builtType", stack.partType);
            Set(stack, "builtCapacity", 2);
            Set(stack, "count", 2);
            Set(stack, "fillOrder", new[] { 0, 1 });
            Set(stack, "rankOfSlot", new[] { 0, 1 });
            stack.ConfigureReadySlot(Vector3.zero, Quaternion.identity);
            GameObject actor = new GameObject("Worker");
            actor.transform.SetParent(fixture.transform, false);
            Worker worker = actor.AddComponent<Worker>();
            worker.SetHome(Vector3.zero);
            GameObject item = new GameObject("AssemblyPart");
            item.transform.SetParent(fixture.transform, false);
            AssemblyPart part = item.AddComponent<AssemblyPart>();
            part.enabled = false;
            Require(worker.AssignFromReady(part, stack, Vector3.zero, Quaternion.identity, true), "legacy reserve assignment");
            Require(stack.Count == 2 && !part.gameObject.activeSelf, "assignment keeps stock/display and hides vehicle part");
            worker.CancelWork();
            Require(stack.Count == 2, "prepickup cancel does not refund phantom stock");
            Require(worker.AssignFromReady(part, stack, Vector3.zero, Quaternion.identity, true), "legacy reassign");
            typeof(Worker).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(worker, null);
            Require(stack.Count == 1 && part.gameObject.activeSelf && worker.currentState == Worker.WorkerState.MovingToPart,
                "Fetch arrival consumes once and activates carried part");
            worker.CancelWork();
            worker.CancelWork();
            Require(stack.Count == 2 && !part.gameObject.activeSelf, "postpickup cancel refunds exactly once");
            Tick(stack, (float)Get(stack, "readyPickupTime") + 1.001f);
            Require(worker.AssignFromReady(part, stack, Vector3.zero, Quaternion.identity, true, false), "flow assignment");
            typeof(Worker).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(worker, null);
            worker.CancelWork();
            Require(stack.Count == 1 && part.gameObject.activeSelf, "flow cancellation retains consumed unfinished part");
            return "FactoryPickupSlotChecks RunLegacy PASS (hidden fetch, pickup-time stock, pre/post cancel, exactly-once refund, retained flow)";
        }
        finally { UnityEngine.Object.DestroyImmediate(fixture); }
    }

    public static string RunCoreGc()
    {
        GameObject fixture = new GameObject("PickupGcChecks");
        GameObject grip = new GameObject("Grip");
        try
        {
            PartStack stack = fixture.AddComponent<PartStack>();
            stack.enabled = false; stack.capacity = 2; stack.autoFitCollider = false;
            GameObject[] displays = new GameObject[2];
            for (int i = 0; i < displays.Length; i++)
            {
                displays[i] = new GameObject("Display");
                displays[i].transform.SetParent(fixture.transform, false);
            }
            Set(stack, "items", displays); Set(stack, "builtType", stack.partType);
            Set(stack, "builtCapacity", 2); Set(stack, "count", 2);
            Set(stack, "fillOrder", new[] { 0, 1 }); Set(stack, "rankOfSlot", new[] { 0, 1 });
            stack.ConfigureReadySlot(Vector3.zero, Quaternion.identity);
            for (int i = 0; i < 10; i++) GcCycle(stack, fixture, grip.transform);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) GcCycle(stack, fixture, grip.transform);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated == 0, "100 warmed pickup/refill/Add cycles allocate zero managed bytes");
            return "FactoryPickupSlotChecks RunCoreGc PASS (100 cycles, " + allocated + " bytes)";
        }
        finally { UnityEngine.Object.DestroyImmediate(fixture); UnityEngine.Object.DestroyImmediate(grip); }
    }

    private static void GcCycle(PartStack stack, GameObject owner, Transform grip)
    {
        grip.position = stack.ReadyPosition;
        Require(stack.TryTakeReady(owner, grip, out _, out _), "GC fixture pickup");
        grip.position = stack.ReadyPosition + Vector3.right * .51f;
        Require(stack.HasReadyPart, "GC fixture refill");
        Require(stack.Add(1) == 1, "GC fixture supply");
    }

    public static string RunPlayerPool()
    {
        GameObject fixture = new GameObject("PlayerPoolChecks");
        GameObject prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
        PortableAssemblyPart[] pool = new PortableAssemblyPart[2];
        try
        {
            PartStack stack = fixture.AddComponent<PartStack>();
            stack.enabled = false; stack.capacity = 2; stack.autoFitCollider = false;
            stack.partType = PartType.Wheel_FrontRight_41;
            GameObject[] displays = new GameObject[2];
            for (int i = 0; i < displays.Length; i++)
            {
                displays[i] = new GameObject("Display");
                displays[i].transform.SetParent(fixture.transform, false);
                pool[i] = PortableAssemblyPart.Create(stack.partType, prefab, Vector3.zero, Quaternion.identity);
                pool[i].InitializePool(fixture.transform);
            }
            Set(stack, "items", displays); Set(stack, "builtType", stack.partType);
            Set(stack, "builtCapacity", 2); Set(stack, "count", 2);
            Set(stack, "fillOrder", new[] { 0, 1 }); Set(stack, "rankOfSlot", new[] { 0, 1 });
            Set(stack, "portablePool", pool);
            stack.ConfigureReadySlot(Vector3.zero, Quaternion.identity);
            GameObject actor = new GameObject("Player"); actor.transform.SetParent(fixture.transform, false);
            actor.transform.position = Vector3.down * 1.1f;
            PlayerAssemblyInteractor player = actor.AddComponent<PlayerAssemblyInteractor>();
            GameObject hand = new GameObject("Grip"); hand.transform.SetParent(actor.transform, false);
            hand.transform.localPosition = Vector3.up * 1.1f;
            player.holdAnchor = hand.transform;
            Require(player.TryTake(stack) && stack.Count == 1, "actual player takes staged part once");
            PortableAssemblyPart first = player.HeldPart;
            Require(player.TryThrow(), "player releases ownership before recycle");
            Require(!player.TryTake(stack), "player cannot take before slot clearance");
            hand.transform.position = Vector3.right * .51f;
            Require(stack.HasReadyPart, "player grip clearance refills");
            hand.transform.position = Vector3.zero;
            Require(player.TryTake(stack) && stack.Count == 0, "last ready player part");
            PortableAssemblyPart second = player.HeldPart;
            Require(first != second && player.TryThrow(), "bounded pool leases distinct objects");
            Require(stack.Add(2) == 2, "player source supplies after zero");
            Tick(stack, (float)Get(stack, "readyPickupTime") + 1.001f);
            Require(!player.TryTake(stack) && stack.Count == 2, "pool exhaustion rejects without consuming");
            first.ReleaseToPool(); second.ReleaseToPool();
            for (int i = 0; i < 20; i++)
            {
                Require(player.TryTake(stack), "recycled player pickup");
                PortableAssemblyPart reused = player.HeldPart;
                Require(reused == first || reused == second, "pool identity reused");
                Require(player.TryThrow(), "recycled release");
                reused.ReleaseToPool();
                hand.transform.position = Vector3.right * .51f;
                Require(stack.HasReadyPart, "recycled refill");
                hand.transform.position = Vector3.zero;
                Require(stack.Add(1) == 1, "recycled supply");
            }
            return "FactoryPickupSlotChecks RunPlayerPool PASS (actual player, empty cooldown, clearance, zero/supply, exhaustion, 20 identity reuse cycles)";
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(fixture);
            UnityEngine.Object.DestroyImmediate(prefab);
            for (int i = 0; i < pool.Length; i++)
                if (pool[i] != null) UnityEngine.Object.DestroyImmediate(pool[i].gameObject);
        }
    }

    private static FieldInfo Field(string name) => typeof(PartStack).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    private static void Set(PartStack stack, string name, object value) => Field(name).SetValue(stack, value);
    private static object Get(PartStack stack, string name) => Field(name).GetValue(stack);
    private static void Tick(PartStack stack, float now) => typeof(PartStack).GetMethod("TickReadySlot", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(stack, new object[] { now });
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Pickup slot check failed: " + message);
    }
}
#endif
