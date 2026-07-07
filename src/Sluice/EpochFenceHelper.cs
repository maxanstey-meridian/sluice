namespace Sluice;

public static class EpochFenceHelper
{
    public static bool Overlaps(
        ResourceAddress changedAddress,
        IReadOnlyList<ResourceAddress> observedReads
    )
    {
        foreach (var observed in observedReads)
        {
            if (changedAddress == observed)
            {
                return true;
            }
            if (
                changedAddress.Key == "*"
                && changedAddress.Kind == observed.Kind
                && changedAddress.Name == observed.Name
            )
            {
                return true;
            }
        }
        return false;
    }
}
