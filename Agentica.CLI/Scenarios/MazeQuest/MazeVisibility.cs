namespace Agentica.CLI.Scenarios.MazeQuest;

public static class MazeVisibility
{
    public static IEnumerable<MazePoint> VisiblePoints(MazeGrid grid, MazePoint center, int radius)
    {
        for (var y = center.Y - radius; y <= center.Y + radius; y++)
        {
            for (var x = center.X - radius; x <= center.X + radius; x++)
            {
                var point = new MazePoint(x, y);
                if (!grid.Contains(point))
                {
                    continue;
                }

                var distance = Math.Abs(center.X - x) + Math.Abs(center.Y - y);
                if (distance <= radius)
                {
                    yield return point;
                }
            }
        }
    }
}
