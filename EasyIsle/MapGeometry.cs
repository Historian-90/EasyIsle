namespace EasyIsle;

/// <summary>World position to the map coordinates the game shows (the ones in map links).</summary>
public static class MapGeometry
{
    public static float ToMap(float world, float offset, float sizeFactor)
    {
        var c = sizeFactor / 100f;
        return 41f / c * (((world + offset) * c + 1024f) / 2048f) + 1f;
    }

    public static float ToMapLength(float world, float sizeFactor) => world * 41f / 2048f;
}
