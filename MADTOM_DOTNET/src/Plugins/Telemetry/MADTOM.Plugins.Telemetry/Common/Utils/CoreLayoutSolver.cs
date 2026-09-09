using System;

namespace MadTOM.Common.Utils;

public readonly record struct CoreLayoutResult(
    double CellSize,
    double Gap,
    int Cols,
    int Rows,
    double GridWidth,
    double GridHeight,
    double StartX,
    double StartY);

public static class CoreLayoutSolver
{
    public static CoreLayoutResult ComputeVerticalFillSquareLayout(double availW, double availH, int coreCount)
    {
        if (coreCount <= 0) coreCount = 1;
        availW = Math.Max(20, availW);
        availH = Math.Max(20, availH);

        double bestScore = double.NegativeInfinity;
        CoreLayoutResult? best = null;

        double[] candidateGaps = [1.0, 0.8, 1.2];
        foreach (double gap in candidateGaps)
        {
            int maxCols = Math.Min(coreCount, 256);
            for (int cols = 1; cols <= maxCols; cols++)
            {
                int rows = (int)Math.Ceiling((double)coreCount / cols);

                double maxS_W = (availW - (cols - 1) * gap) / cols;
                double maxS_H = (availH - (rows - 1) * gap) / rows;
                double s = Math.Min(maxS_W, maxS_H);

                if (s < 1.0) continue;

                double gridW = cols * s + (cols - 1) * gap;
                double gridH = rows * s + (rows - 1) * gap;

                double fillW = gridW / availW;
                double fillH = gridH / availH;

                double score = fillH * 200.0 + fillW * 150.0 + s;
                if (fillH < 0.75) score -= (0.75 - fillH) * 500.0;
                if (fillW < 0.75) score -= (0.75 - fillW) * 500.0;

                if (score > bestScore)
                {
                    bestScore = score;
                    double startX = Math.Max(0.0, (availW - gridW) / 2.0);
                    double startY = Math.Max(0.0, (availH - gridH) / 2.0);
                    best = new CoreLayoutResult(s, gap, cols, rows, gridW, gridH, startX, startY);
                }
            }
        }

        if (best.HasValue)
        {
            return best.Value;
        }

        // Fallback layout
        const double fallbackGap = 0.8;
        int fallbackRows = Math.Max(1, Math.Min(16, (int)Math.Ceiling(Math.Sqrt(coreCount * availH / availW))));
        int fallbackCols = (int)Math.Ceiling((double)coreCount / fallbackRows);
        double fallbackS = Math.Max(1.0, Math.Min((availW - (fallbackCols - 1) * fallbackGap) / fallbackCols,
                                                  (availH - (fallbackRows - 1) * fallbackGap) / fallbackRows));
        double fGridW = fallbackCols * fallbackS + (fallbackCols - 1) * fallbackGap;
        double fGridH = fallbackRows * fallbackS + (fallbackRows - 1) * fallbackGap;
        double fStartX = Math.Max(0.0, (availW - fGridW) / 2.0);
        double fStartY = Math.Max(0.0, (availH - fGridH) / 2.0);

        return new CoreLayoutResult(fallbackS, fallbackGap, fallbackCols, fallbackRows, fGridW, fGridH, fStartX, fStartY);
    }
}

