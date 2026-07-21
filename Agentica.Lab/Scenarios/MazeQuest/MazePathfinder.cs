namespace Agentica.Lab.Scenarios.MazeQuest;

public static class MazePathfinder
{
    public static IReadOnlyDictionary<MazePoint, int> Distances(
        MazeGrid grid,
        MazePoint start,
        Func<MazeCell, bool>? canEnter = null)
    {
        canEnter ??= cell => cell.Terrain != MazeTerrain.Wall;

        var distances = new Dictionary<MazePoint, int>
        {
            [start] = 0
        };
        var queue = new Queue<MazePoint>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var point = queue.Dequeue();
            var distance = distances[point];

            foreach (var neighbor in Neighbors(point))
            {
                if (!grid.Contains(neighbor) ||
                    distances.ContainsKey(neighbor) ||
                    !canEnter(grid[neighbor]))
                {
                    continue;
                }

                distances[neighbor] = distance + 1;
                queue.Enqueue(neighbor);
            }
        }

        return distances;
    }

    public static IReadOnlyList<MazePoint> ShortestPath(
        MazeGrid grid,
        MazePoint start,
        MazePoint target,
        Func<MazeCell, bool>? canEnter = null)
    {
        canEnter ??= cell => cell.Terrain != MazeTerrain.Wall;

        var previous = new Dictionary<MazePoint, MazePoint>();
        var seen = new HashSet<MazePoint> { start };
        var queue = new Queue<MazePoint>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var point = queue.Dequeue();
            if (point == target)
            {
                break;
            }

            foreach (var neighbor in Neighbors(point))
            {
                if (!grid.Contains(neighbor) ||
                    seen.Contains(neighbor) ||
                    !canEnter(grid[neighbor]))
                {
                    continue;
                }

                previous[neighbor] = point;
                seen.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }

        if (!seen.Contains(target))
        {
            return [];
        }

        var path = new List<MazePoint> { target };
        var cursor = target;
        while (cursor != start)
        {
            cursor = previous[cursor];
            path.Add(cursor);
        }

        path.Reverse();
        return path;
    }

    public static IEnumerable<MazePoint> Neighbors(MazePoint point)
    {
        yield return point.Translate(0, -1);
        yield return point.Translate(1, 0);
        yield return point.Translate(0, 1);
        yield return point.Translate(-1, 0);
    }
}
