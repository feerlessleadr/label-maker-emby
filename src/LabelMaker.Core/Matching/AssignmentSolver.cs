using System;

namespace LabelMaker.Core.Matching
{
    /// <summary>
    /// Finds the best one-to-one pairing of files with tracks (the Hungarian algorithm). Used so that a title that appears twice
    /// on one release (for example the same song on two discs) is given to the right file, and so that no two files claim one track.
    /// </summary>
    public static class AssignmentSolver
    {
        /// <summary>
        /// For a matrix of scores (rows = files, columns = tracks; higher is better) returns, for every row, the column it is paired
        /// with, or -1 when there are more rows than columns and that row got nothing. The total score is as high as it can be.
        /// </summary>
        public static int[] MaximiseScore(double[,] scores)
        {
            int rows = scores.GetLength(0);
            int cols = scores.GetLength(1);
            var result = new int[rows];
            for (int i = 0; i < rows; i++) result[i] = -1;
            if (rows == 0 || cols == 0) return result;

            if (rows <= cols)
            {
                var cost = new double[rows, cols];
                for (int i = 0; i < rows; i++) for (int j = 0; j < cols; j++) cost[i, j] = -scores[i, j];
                return Solve(cost);
            }

            // More files than tracks: pair every track with a file, then read the pairing the other way round.
            var transposed = new double[cols, rows];
            for (int i = 0; i < rows; i++) for (int j = 0; j < cols; j++) transposed[j, i] = -scores[i, j];
            var columnToRow = Solve(transposed);
            for (int j = 0; j < cols; j++) result[columnToRow[j]] = j;
            return result;
        }

        /// <summary>Minimum-cost assignment for a matrix with no more rows than columns. Returns the column of each row.</summary>
        private static int[] Solve(double[,] cost)
        {
            int n = cost.GetLength(0);
            int m = cost.GetLength(1);
            var u = new double[n + 1];
            var v = new double[m + 1];
            var p = new int[m + 1];
            var way = new int[m + 1];

            for (int i = 1; i <= n; i++)
            {
                p[0] = i;
                int j0 = 0;
                var minv = new double[m + 1];
                var used = new bool[m + 1];
                for (int j = 0; j <= m; j++) minv[j] = double.PositiveInfinity;
                do
                {
                    used[j0] = true;
                    int i0 = p[j0];
                    double delta = double.PositiveInfinity;
                    int j1 = 0;
                    for (int j = 1; j <= m; j++)
                    {
                        if (used[j]) continue;
                        double current = cost[i0 - 1, j - 1] - u[i0] - v[j];
                        if (current < minv[j]) { minv[j] = current; way[j] = j0; }
                        if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                    }
                    for (int j = 0; j <= m; j++)
                    {
                        if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                        else minv[j] -= delta;
                    }
                    j0 = j1;
                } while (p[j0] != 0);

                do
                {
                    int j1 = way[j0];
                    p[j0] = p[j1];
                    j0 = j1;
                } while (j0 != 0);
            }

            var rowToColumn = new int[n];
            for (int j = 1; j <= m; j++)
            {
                if (p[j] != 0) rowToColumn[p[j] - 1] = j - 1;
            }
            return rowToColumn;
        }
    }
}
