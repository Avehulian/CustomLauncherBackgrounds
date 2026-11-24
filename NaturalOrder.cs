namespace PreyToLive.CustomLauncherBackgrounds;

/// <summary>
/// Natural file name ordering: digit runs compare numerically, everything else case-insensitively,
/// so "2-bg.png" comes before "10-bg.png". Used for the playlist and the media lists.
/// </summary>
internal static class NaturalOrder
{
    public static int Compare(string? a, string? b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        var ia = 0;
        var ib = 0;

        while (ia < a.Length && ib < b.Length)
        {
            var digitA = char.IsDigit(a[ia]);
            var digitB = char.IsDigit(b[ib]);

            if (digitA && digitB)
            {
                var startA = ia;
                while (ia < a.Length && char.IsDigit(a[ia]))
                {
                    ia++;
                }

                var startB = ib;
                while (ib < b.Length && char.IsDigit(b[ib]))
                {
                    ib++;
                }

                var numberA = a.AsSpan(startA, ia - startA).TrimStart('0');
                var numberB = b.AsSpan(startB, ib - startB).TrimStart('0');
                var comparison = numberA.Length != numberB.Length
                    ? numberA.Length.CompareTo(numberB.Length)
                    : numberA.SequenceCompareTo(numberB);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
            else
            {
                var charA = char.ToLowerInvariant(a[ia]);
                var charB = char.ToLowerInvariant(b[ib]);
                if (charA != charB)
                {
                    return charA.CompareTo(charB);
                }

                ia++;
                ib++;
            }
        }

        return (a.Length - ia).CompareTo(b.Length - ib);
    }
}
