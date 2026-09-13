namespace LaunchpadClone.Core.Groups;

/// <summary>
/// A user-created launcher group (a "folder" in macOS Launchpad terms).
/// Members are referenced by <c>AppItem.Id</c> so the group survives icon
/// and cache changes; missing ids are ignored everywhere they are read.
/// </summary>
public sealed class AppGroup
{
    /// <summary>Stable random id — groups are identified by this, not by name.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New Group";

    public List<string> MemberIds { get; set; } = new();
}
