using System.Windows;
using System.Windows.Controls;

namespace LaunchpadClone.UI;

/// <summary>
/// Chooses between the app tile and the group ("folder") tile template
/// based on the row type in the paged grid.
/// </summary>
public sealed class TileTemplateSelector : DataTemplateSelector
{
    public DataTemplate? AppTemplate { get; set; }
    public DataTemplate? GroupTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is MainWindow.GroupRow ? GroupTemplate : AppTemplate;
}
