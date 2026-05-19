namespace Agentica.CLI.Scenarios.ChessQuest;

public static class ChessQuestRenderer
{
    public static string RenderBoardFromFen(string fen) =>
        string.Join(Environment.NewLine, RenderBoardLinesFromFen(fen));

    public static IReadOnlyList<string> RenderBoardLinesFromFen(string fen)
    {
        var placement = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var ranks = placement.Split('/');
        if (ranks.Length != 8)
        {
            return ["invalid fen board"];
        }

        var lines = new List<string>(9);
        for (var rankIndex = 0; rankIndex < 8; rankIndex++)
        {
            var rank = 8 - rankIndex;
            var cells = new List<string>(8);
            foreach (var ch in ranks[rankIndex])
            {
                if (char.IsDigit(ch))
                {
                    for (var i = 0; i < ch - '0'; i++)
                    {
                        cells.Add(".");
                    }
                }
                else
                {
                    cells.Add(ch.ToString());
                }
            }

            lines.Add($"{rank} {string.Join(' ', cells.Take(8))}");
        }

        lines.Add("  a b c d e f g h");
        return lines;
    }
}
