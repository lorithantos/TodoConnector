namespace TodoMcp.Tests;

/// <summary>House rule 5: no tuple in any signature, at any visibility, in the server or its tests.</summary>
public sealed class NoTupleSignaturesTests
{
    [Fact]
    public void TheServerAssemblyHasNoTupleInAnySignature()
    {
        Assert.Empty(TupleSignatureScanner.Find(typeof(TodoClock).Assembly));
    }

    [Fact]
    public void ThisTestAssemblyHasNoTupleInAnySignature()
    {
        Assert.Empty(TupleSignatureScanner.Find(typeof(NoTupleSignaturesTests).Assembly));
    }

    [Fact]
    public void TheScannerFindsATupleField()
    {
        // The scanner must be able to fail, or the two tests above prove nothing.
        IReadOnlyList<string> found = TupleSignatureScanner.FindIn(typeof(TupleCarrier));

        Assert.Contains(found, f => f.Contains(nameof(TupleCarrier), StringComparison.Ordinal));
    }

    /// <summary>The scanner's own positive control. Excluded from the assembly scan by name.</summary>
    internal sealed class TupleCarrier
    {
        internal (int First, int Second) Pair = (1, 2);
    }
}
