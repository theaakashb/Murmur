// Port of app/EditTrigger.swift.
namespace Murmur.Core;

public static class EditTrigger
{
    public const string SameKey = "same";

    public static bool CapturesKey(bool secureInput, string? selection) =>
        !secureInput && selection is not null && SwiftText.TrimWhitespaceAndNewlines(selection).Length != 0;
}
