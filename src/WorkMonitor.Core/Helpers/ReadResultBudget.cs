using System.Text.Json;

namespace WorkMonitor.Core.Helpers;

public static class ReadResultBudget
{
    public const int MaxCharacters = 14000;

    public static TResult FitCollection<TItem, TResult>(
        IReadOnlyList<TItem> items,
        Func<IReadOnlyList<TItem>, bool, TResult> createResult)
    {
        for (var count = items.Count; count >= 0; count--)
        {
            var retained = items.Take(count).ToArray();
            var result = createResult(retained, count < items.Count);
            if (JsonSerializer.Serialize(result).Length <= MaxCharacters)
            {
                return result;
            }
        }

        throw new InvalidOperationException(
            "Result metadata exceeds the read-only response budget.");
    }
}
