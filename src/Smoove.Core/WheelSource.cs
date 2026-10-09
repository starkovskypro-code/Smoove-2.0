namespace Smoove.Core;

public static class WheelSource
{
    // Extra=0 is ordinary input, including the injected proxy/driver source observed live.
    // An injected flag alone must not bypass the product. Own events always bypass it.
    public static bool Transformable(bool injected, nuint extra, nuint ownMarker) =>
        extra != ownMarker && (!injected || extra == 0);
}
