namespace KissakiViewer.Core.NameRecovery;

/// <summary>
/// KatanaEngine KTID hash function.
/// Reverse-engineered from DOA6/KtidFile.cpp (eterniti/eternity_common).
/// Polynomial rolling hash with multiplier=31, seeds (a2=0, a3=1).
/// </summary>
public static class KtidHasher
{
    public static uint Compute(string s) => ComputeBytes(s, 0, 1);

    public static uint ComputeBytes(string s, uint a2, uint a3)
    {
        foreach (char ch in s)
        {
            uint v3 = a3;
            uint c  = (byte)ch;
            a3 *= 31;
            a2 += 31 * v3 * c;
        }
        return a2;
    }

    // Verify: known TypeKtid values let us sanity-check the seed choice.
    // Call this from a test to see which seeds produce known matches.
    public static uint ComputeWithSeeds(string s, uint seed1, uint seed2)
        => ComputeBytes(s, seed1, seed2);
}
