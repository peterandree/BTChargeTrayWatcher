using System.Runtime.CompilerServices;

namespace Xunit
{
    public sealed class StaFactAttribute : FactAttribute
    {
        public StaFactAttribute(
            [CallerFilePath] string? sourceFilePath = null,
            [CallerLineNumber] int sourceLineNumber = 0)
            : base(sourceFilePath, sourceLineNumber) { }
    }
}