using UnityEngine;
using UnityEngine.UI;

/// <summary>Top-down projection of the open portion of the same spline that carries the cars.</summary>
[RequireComponent(typeof(CanvasRenderer))]
public sealed class FactoryTabletRoute : MaskableGraphic
{
    const int Segments = 96;
    readonly Vector2[] path = new Vector2[Segments + 1];
    Vector2[] stations;
    Vector2 wash, outlet, minimum, span;
    FirstProductionLoop source;
    int opened, outletPrefix;
    public void UpdateLayout(FirstProductionLoop loop)
    {
        if (source == loop && opened == loop.OpenedProcesses && outletPrefix == loop.OutletPrefix) return;
        source = loop;
        opened = loop.OpenedProcesses;
        outletPrefix = loop.OutletPrefix;
        var spline = loop.progression.mainSpline;
        float end = loop.TabletOpenedEnd;
        Vector2 Plan(float progress)
        {
            var p = spline.EvaluatePosition(progress);
            return new Vector2(p.x, p.z);
        }
        minimum = Plan(0);
        var maximum = minimum;
        for (int i = 0; i <= Segments; i++)
        {
            path[i] = Plan(end * i / Segments);
            minimum = Vector2.Min(minimum, path[i]);
            maximum = Vector2.Max(maximum, path[i]);
        }
        span = maximum - minimum;
        wash = Plan(loop.WashProgress);
        outlet = Plan(loop.TabletOutletEnd);
        stations = new Vector2[opened];
        for (int i = 0; i < opened; i++) stations[i] = Plan(loop.progression.processes[i].gateProgress);
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper mesh)
    {
        mesh.Clear();
        if (source == null || stations == null) return;
        Rect area = rectTransform.rect;
        float scale = Mathf.Min((area.width - 20f) / Mathf.Max(.01f, span.x), (area.height - 20f) / Mathf.Max(.01f, span.y));
        Vector2 Project(Vector2 p) => area.center + (p - minimum - span * .5f) * scale;
        for (int i = 1; i <= Segments; i++)
        {
            Vector2 a = Project(path[i - 1]), b = Project(path[i]);
            var normal = Vector2.Perpendicular((b - a).normalized) * 2.2f;
            Quad(mesh, a - normal, a + normal, b + normal, b - normal, new Color(.38f, .58f, .63f));
        }
        foreach (var station in stations) Dot(mesh, Project(station), 3.8f, Color.white);
        Dot(mesh, Project(wash), 6f, new Color(.40f, .91f, .86f));
        Dot(mesh, Project(outlet), 6f, new Color(1f, .75f, .35f));
    }

    static void Dot(VertexHelper mesh, Vector2 p, float radius, Color color)
    {
        Quad(mesh, p + Vector2.left * radius, p + Vector2.up * radius, p + Vector2.right * radius, p + Vector2.down * radius, color);
    }

    static void Quad(VertexHelper mesh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
    {
        int i = mesh.currentVertCount;
        mesh.AddVert(a, color, Vector2.zero); mesh.AddVert(b, color, Vector2.zero);
        mesh.AddVert(c, color, Vector2.zero); mesh.AddVert(d, color, Vector2.zero);
        mesh.AddTriangle(i, i + 1, i + 2); mesh.AddTriangle(i, i + 2, i + 3);
    }
}
