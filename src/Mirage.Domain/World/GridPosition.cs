namespace Mirage.Domain.World;

public readonly record struct GridPosition(int X, int Y)
{
    public int ChebyshevDistanceTo(GridPosition other) =>
        Math.Max(Math.Abs(other.X - X), Math.Abs(other.Y - Y));
}
