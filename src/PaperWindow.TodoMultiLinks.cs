using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private IEnumerable<TodoQuickLaunchLink> VisibleTodoLinks(PaperItem item) =>
        item.GetQuickLaunchLinks().Where(link => link.PaperId == null ||
            (_controller.State.EnableTodoPaperLinks && _controller.IsExistingPaper(link.PaperId)));

    private void AddTodoLinks(PaperItem item, IEnumerable<TodoQuickLaunchLink> targets)
    {
        var additions = targets.Where(target => !item.HasQuickLaunchLink(target)).ToArray();
        if (additions.Length == 0) return;
        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        var before = CloneItems(_paper.Items);
        PushUndoSnapshot();
        item.RestoreQuickLaunchLinks(item.GetQuickLaunchLinks().Concat(additions).ToArray());
        _controller.MarkDirty();
        ReconcileTodoRows([item.Id], focusedId);
        RefreshCapsuleEligibilityForLinkedPaperChanges(before);
    }

    private void RemoveTodoLink(PaperItem item, TodoQuickLaunchLink link)
    {
        var focusedId = CurrentFocusedTodoItemId() ?? item.Id;
        var before = CloneItems(_paper.Items);
        PushUndoSnapshot();
        item.RemoveQuickLaunchLink(link);
        _controller.MarkDirty();
        ReconcileTodoRows([item.Id], focusedId);
        RefreshCapsuleEligibilityForLinkedPaperChanges(before);
    }

    private string TodoLinkLabel(TodoQuickLaunchLink link) => link.PaperId != null
        ? (_controller.TryGetLinkedPaperTitle(link.PaperId, out var title) ? title : link.PaperId)
        : PathDisplayName(link.Path ?? "");

    private void OpenTodoLink(TodoQuickLaunchLink link, bool editPaper = false)
    {
        if (link.PaperId != null)
        {
            if (editPaper)
            {
                _controller.OpenLinkedPaper(link.PaperId, this);
                return;
            }
            if (!_controller.ShouldRunLinkedScriptCapsule(link.PaperId) ||
                !_controller.RunLinkedScriptCapsule(link.PaperId))
                _controller.OpenLinkedPaper(link.PaperId, this, toggleIfExcludedFromCapsules: true);
        }
        else
        {
            OpenTodoLinkedPath(link.Path);
        }
    }

    private void PopulateTodoLinksMenu(ItemsControl menu, PaperItem item, bool allowUnlink)
    {
        foreach (var link in VisibleTodoLinks(item))
        {
            var entry = MenuItem(TodoLinkLabel(link), (_, _) => { if (!allowUnlink) OpenTodoLink(link); });
            if (_controller.State.EnableToolTips) entry.ToolTip = link.Path ?? TodoLinkLabel(link);
            menu.Items.Add(entry);
            if (!allowUnlink) continue;
            // Management lives in the row's context menu; the left-click list opens targets.
            var remove = MenuItem(link.PaperId != null ? Strings.Get("MenuUnlinkPaper") :
                Strings.Get("MenuUnlinkPath"), (_, _) => RemoveTodoLink(item, link));
            entry.Items.Add(MenuItem(link.PaperId != null ? Strings.Format(_controller.ShouldRunLinkedScriptCapsule(link.PaperId)
                ? "MenuEditLinkedScriptCapsule" : "MenuOpenLinkedPaper", TodoLinkLabel(link)) :
                Strings.Format("MenuOpenLinkedPath", TodoLinkLabel(link)), (_, _) => OpenTodoLink(link, editPaper: true)));
            if (link.Path != null)
            {
                entry.Items.Add(MenuItem(Strings.Get("MenuOpenLinkedPathLocation"), (_, _) =>
                    OpenTodoLinkedPathLocation(link.Path)));
            }
            entry.Items.Add(remove);
        }
    }

    private void ShowTodoLinksMenu(PaperItem item, FrameworkElement? placementTarget = null)
    {
        var menu = CreateContextMenu();
        PopulateTodoLinksMenu(menu, item, allowUnlink: false);
        menu.PlacementTarget = placementTarget ?? this;
        menu.Placement = placementTarget == null ? PlacementMode.MousePoint : PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private Border BuildTodoLinksButton(PaperItem item, TodoVisualMetrics metrics)
    {
        var multiple = VisibleTodoLinks(item).Skip(1).Any();
        var glyph = new TextBlock
        {
            Text = multiple ? "▾" : "↗",
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = metrics.LinkedPaperIconFontSize
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "LinkBrushKey");
        var button = new Border
        {
            MinWidth = Math.Max(23, metrics.CheckColumnWidth),
            MinHeight = Math.Max(22, metrics.RowMinHeight - 2),
            Margin = new Thickness(1, 0, 0, 0),
            CornerRadius = new CornerRadius(RadiusControl),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = glyph
        };
        if (_controller.State.EnableToolTips) button.ToolTip = Strings.Get("MenuTodoLinks");
        button.MouseEnter += (_, _) => button.Background = HoverBrush;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        button.MouseLeftButtonUp += (_, e) =>
        {
            var links = VisibleTodoLinks(item).ToArray();
            if (links.Length == 1) OpenTodoLink(links[0]);
            else if (links.Length > 1) ShowTodoLinksMenu(item, button);
            e.Handled = true;
        };
        return button;
    }
}
