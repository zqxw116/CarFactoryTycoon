using UnityEngine;
using UnityEngine.AI;

/// <summary>Reserves unfinished production bays while keeping the east warehouse aisle open.</summary>
public sealed class FactoryProductionFence : MonoBehaviour
{
    // Allow the car and the full side-transfer deck to clear the last operating station.
    const float DeliveryClearance = 4.5f;
    const float RowClearance = 6.5f;
    FactoryExpansion building;
    FenceArea futureRows;
    FenceArea remainingBay;

    public void Initialize(FactoryExpansion expansion)
    {
        building = expansion;
        var steel = building.walls[0].GetComponent<Renderer>().sharedMaterial;
        var yellow = building.wallCaps[0].GetComponent<Renderer>().sharedMaterial;
        futureRows = new FenceArea(transform, "미개방 생산 줄", steel, yellow);
        remainingBay = new FenceArea(transform, "미개방 작업 구역", steel, yellow);
    }

    int FirstInRow(int index)
    {
        var entries = building.progression.processes;
        while (index > 0 && entries[index - 1].row == entries[index].row) index--;
        return index;
    }

    public float RowEdge(int index)
    {
        int first = FirstInRow(index);
        var p = building.progression;
        return p.mainSpline.EvaluatePosition(p.processes[first].gateProgress).z - RowClearance;
    }

    public void SetOpenArea(int prefix, Vector3 outlet, float aisleX)
    {
        var p = building.progression;
        var bounds = building.ActiveBounds;
        float west = bounds.min.x;
        float east = Mathf.Clamp(aisleX, west, bounds.max.x);
        int last = prefix - 1;
        int first = FirstInRow(last);
        float south = Mathf.Clamp(RowEdge(last), bounds.min.z, bounds.max.z);
        float north = first == 0 ? bounds.max.z : RowEdge(first - 1);
        float floorY = building.floor.transform.position.y;
        bool complete = prefix == p.processes.Length;
        futureRows.Place(west, east, bounds.min.z, south, floorY, !complete);

        // The first bay runs west. Read each row's authored spline direction for the return bays.
        bool travelsEast = p.mainSpline.EvaluateTangent(p.processes[first].gateProgress).x > 0;
        float edge = Mathf.Clamp(outlet.x + (travelsEast ? DeliveryClearance : -DeliveryClearance), west, east);
        bool hasNextInRow = !complete && p.processes[prefix].row == p.processes[last].row;
        remainingBay.Place(travelsEast ? edge : west, travelsEast ? east : edge, south, north, floorY, hasNextInRow);
    }

    sealed class FenceArea
    {
        readonly Transform root;
        readonly NavMeshObstacle obstacle;
        readonly Transform[] panels = new Transform[4];
        const float Height = 2.5f;

        public FenceArea(Transform parent, string name, Material steel, Material yellow)
        {
            root = new GameObject(name).transform;
            root.SetParent(parent, false);
            obstacle = root.gameObject.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.carving = true;
            obstacle.carveOnlyStationary = false;
            for (int i = 0; i < panels.Length; i++)
            {
                var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
                panel.name = "안전 펜스";
                panels[i] = panel.transform;
                panels[i].SetParent(root, false);
                panel.GetComponent<Renderer>().sharedMaterial = steel;
                var cap = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cap.name = "안전 황색 띠";
                cap.transform.SetParent(panels[i], false);
                cap.transform.localPosition = Vector3.up * .42f;
                cap.transform.localScale = new Vector3(1.005f, .09f, 1.015f);
                cap.GetComponent<Renderer>().sharedMaterial = yellow;
                cap.GetComponent<Collider>().enabled = false;
            }
        }

        public void Place(float west, float east, float south, float north, float floorY, bool visible)
        {
            float width = east - west;
            float depth = north - south;
            root.gameObject.SetActive(visible && width > .2f && depth > .2f);
            if (!root.gameObject.activeSelf) return;
            root.position = new Vector3((west + east) * .5f, floorY + Height * .5f, (north + south) * .5f);
            obstacle.size = new Vector3(width, Height, depth);
            for (int i = 0; i < panels.Length; i++)
            {
                bool across = i < 2;
                panels[i].localPosition = across
                    ? Vector3.forward * ((i == 0 ? -.5f : .5f) * depth)
                    : Vector3.right * ((i == 2 ? -.5f : .5f) * width);
                panels[i].localScale = across ? new Vector3(width, Height, .12f) : new Vector3(.12f, Height, depth);
            }
        }
    }
}
