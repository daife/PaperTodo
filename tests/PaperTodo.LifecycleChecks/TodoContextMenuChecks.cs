using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaperTodo;
using PaperTodo.Plugin;

internal static class TodoContextMenuChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string PaperId = "context-menu-todo";
    private const string LinkedPaperId = "context-menu-note";

    internal static void Prepare(AppState state)
    {
        state.Papers.Clear();
        state.EnableAnimations = false;
        state.UseCapsuleMode = false;
        state.UseDeepCapsuleMode = false;
        state.ExperimentalEdgeCapsuleHoverPreview = false;
        state.ExperimentalTodoReminders = true;
        state.ExperimentalTodoReminderShowButton = true;
        state.EnableTodoPaperLinks = true;
        state.ShowTodoBottomBar = true;
        var path = Path.Combine(AppContext.BaseDirectory, "context-menu-target.txt");
        File.WriteAllText(path, "isolated context-menu target");
        var items = new[] { "mouse", "keyboard", "apps", "path", "paper", "reminder", "done", "multi" }
            .Select((id, index) => new PaperItem { Id = id, Text = "Todo " + id, Order = index })
            .ToList();
        items[3].LinkPath(path, isDirectory: false);
        items[4].LinkPaper(LinkedPaperId);
        items[5].ReminderAt = DateTimeOffset.Now.AddDays(2);
        items[6].Done = true;
        items[7].LinkPaper(LinkedPaperId);
        items[7].AddQuickLaunchLink(new(null, path, false));
        items[7].AddQuickLaunchLink(new(null, AppContext.BaseDirectory, true));
        var area = SystemParameters.WorkArea;
        state.Papers.Add(new PaperData
        {
            Id = PaperId, Type = PaperTypes.Todo, Items = items,
            IsVisible = true, IsCollapsed = false,
            X = area.Left + 40, Y = area.Top + 40, Width = 640, Height = 500
        });
        state.Papers.Add(new PaperData
        {
            Id = LinkedPaperId, Type = PaperTypes.Note, Title = "Linked menu fixture",
            Content = "linked note", IsVisible = false, IsCollapsed = false,
            X = area.Left + 60, Y = area.Top + 60, Width = 300, Height = 240
        });
    }

    internal static async Task RunMultiLinks(AppController controller, IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var window = windows[PaperId];
        var paper = controller.State.Papers.Single(paper => paper.Id == PaperId);
        await Flush();
        var rows = (List<Border>)Part(window, "_todoRows");
        var multiButton = ((Grid)rows.Single(row => row.Tag as string == "multi").Child).Children.OfType<Border>()
            .Single(border => Grid.GetColumn(border) == 2);
        Require(((TextBlock)multiButton.Child).Text == "▾", "multiple targets did not show an expand glyph");
        multiButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        await Flush();
        var menu = ((List<WeakReference<ContextMenu>>)Part(window, "_themedContextMenus"))
            .Select(reference => reference.TryGetTarget(out var candidate) ? candidate : null)
            .OfType<ContextMenu>().Single(candidate => candidate.IsOpen);
        Require(menu.Items.OfType<MenuItem>().Count() == 3,
            "links panel did not list every paper, file and folder");
        Require(menu.Template != null && menu.PlacementTarget == multiButton,
            "links panel did not use the themed menu at the expand button");
        menu.IsOpen = false;
        await Flush();
        var editors = (Dictionary<string, TodoTextBox>)Part(window, "_todoEditors");
        LifecycleInput.Focus(window, editors["multi"]);
        LifecycleInput.KeyChord(0x5D);
        await Until(() => editors["multi"].ContextMenu!.IsOpen, "multiple links context menu");
        menu = editors["multi"].ContextMenu!;
        var linksMenu = Item(menu, Strings.Get("MenuTodoLinks"));
        Require(linksMenu.Items.Count == 3, "context menu lost linked targets");
        var linkedEntry = linksMenu.Items.OfType<MenuItem>().First();
        var unlink = linkedEntry.Items.OfType<MenuItem>().Single(entry => Header(entry) == Strings.Get("MenuUnlinkPaper"));
        menu.IsOpen = false;
        await Flush();
        unlink.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Flush();
        var multi = paper.Items.Single(item => item.Id == "multi");
        Require(multi.GetQuickLaunchLinks().Count() == 2 && !multi.LinkedPaperIds.Any(),
            "unlinking one target removed unrelated links");
        Require(!controller.State.Papers.Single(paper => paper.Id == LinkedPaperId).IsVisible,
            "unlink bubbled into the parent open action");
        var beforeBatch = multi.GetQuickLaunchLinks().ToArray();
        var undo = (List<List<PaperItem>>)Part(window, "_undoStack");
        var undoCount = undo.Count;
        typeof(PaperWindow).GetMethod("AddTodoLinks", Private)!.Invoke(window,
            [multi, new TodoQuickLaunchLink[] { new(null, Path.Combine(AppContext.BaseDirectory, "batch-a.txt"), false),
                new(null, Path.Combine(AppContext.BaseDirectory, "batch-b.txt"), false) }]);
        Require(undo.Count == undoCount + 1 && multi.GetQuickLaunchLinks().Count() == 4,
            "batch linking did not form one history step");
        typeof(PaperWindow).GetMethod("Undo", Private)!.Invoke(window, null);
        multi = paper.Items.Single(item => item.Id == "multi");
        Require(multi.GetQuickLaunchLinks().SequenceEqual(beforeBatch), "one undo did not remove the entire batch");
        var mcp = new McpCommandService(controller, controller.PaperCommands);
        controller.State.McpEnabled = true;
        controller.State.McpAllowFullWrites = false;
        object? Call(object parameters) => mcp.Execute(JsonSerializer.SerializeToElement(new { method = "update_todo", @params = parameters }));
        void Denied(object parameters, string code)
        {
            var before = multi.GetQuickLaunchLinks().ToArray();
            try { Call(parameters); throw new InvalidOperationException("MCP link update unexpectedly succeeded"); }
            catch (McpApiException ex) { Require(ex.Code == code, "MCP link update reported " + ex.Code); }
            Require(multi.GetQuickLaunchLinks().SequenceEqual(before), "rejected MCP update changed links");
        }
        var file = Path.Combine(AppContext.BaseDirectory, "context-menu-target.txt");
        var mixed = new { paper_id = PaperId, todo_id = "multi", add_links = new object[] {
            new { paper_id = LinkedPaperId }, new { path = file }, new { path = AppContext.BaseDirectory } } };
        Denied(mixed, "full_writes_disabled");
        controller.State.McpAllowFullWrites = true;
        Call(mixed);
        Require(multi.GetQuickLaunchLinks().Count() == 3 && multi.LinkedPaperIds.Single() == LinkedPaperId,
            "MCP did not append mixed targets and deduplicate existing paths");
        Call(mixed);
        Require(multi.GetQuickLaunchLinks().Count() == 3, "repeating MCP add duplicated targets");
        Denied(new { paper_id = PaperId, todo_id = "multi", add_links = new object[] { new { path = "relative.txt" } } }, "invalid_params");
        Denied(new { paper_id = PaperId, todo_id = "multi", add_links = new object[] { new { paper_id = PaperId } } }, "self_link_not_allowed");
        Denied(new { paper_id = PaperId, todo_id = "multi", linked_paper_id = LinkedPaperId, clear_links = true }, "invalid_params");
        Denied(new { paper_id = PaperId, todo_id = "multi", add_links = new object[] { new { path = Path.Combine(AppContext.BaseDirectory, "not-found.txt") } } }, "path_not_found");
        var missing = Path.Combine(AppContext.BaseDirectory, "temporary-link.txt");
        File.WriteAllText(missing, "temporary linked file");
        Call(new { paper_id = PaperId, todo_id = "multi", add_links = new object[] { new { path = missing } } });
        File.Delete(missing);
        Call(new { paper_id = PaperId, todo_id = "multi", remove_links = new object[] { new { path = missing } } });
        Require(multi.GetQuickLaunchLinks().Count() == 3, "MCP could not unlink a missing file");
        Call(new { paper_id = PaperId, todo_id = "multi", clear_links = true, add_links = new object[] {
            new { path = file }, new { path = AppContext.BaseDirectory } } });
        Require(multi.GetQuickLaunchLinks().Count() == 2 && !multi.LinkedPaperIds.Any(), "MCP clear-and-add was not atomic replacement");
        window.LinkPaperToTodo("multi", LinkedPaperId);
        controller.PaperCommands.DeletePaper(LinkedPaperId, PaperOperationContext.Mcp());
        Require(multi.GetQuickLaunchLinks().Count() == 2 && multi.GetQuickLaunchLinks().All(link => link.Path != null),
            "deleting a secondary paper target removed unrelated paths");
        typeof(PaperWindow).GetMethod("Undo", Private)!.Invoke(window, null);
        Require(paper.Items.Single(item => item.Id == "multi").LinkedPaperIds.All(id => id != LinkedPaperId),
            "undo resurrected a deleted paper reference");
        Console.WriteLine("PASS multiple Todo links: rendered panel, context unlink, secondary-paper deletion and history pruning");
    }

    internal static async Task Run(AppController controller, IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var window = windows[PaperId];
        var rows = (List<Border>)Part(window, "_todoRows");
        var editors = (Dictionary<string, TodoTextBox>)Part(window, "_todoEditors");
        var selected = (HashSet<string>)Part(window, "_selectedTodoItemIds");
        await Flush();
        LifecycleInput.Focus(window, editors["mouse"]);
        await Flush();
        Require(rows.Count == 8 && rows.All(row => row.ActualHeight > 0), "menu fixture rows were not laid out");

        var invocations = new List<PaperTodoActionInvocation>();
        controller.SetPluginTodoActions(Guid.NewGuid(), "fixture.context-menu", PaperId, "mouse",
            [new PaperTodoAction
            {
                Id = "menu-action", Text = "Fixture plugin action", Icon = PaperTopBarIcon.Character("+"),
                Placement = PaperTodoActionPlacement.ContextMenu
            }], () => true, invocations.Add);
        await Flush();
        ContextMenu? opened = null;
        TodoTextBox? textMenuOwner = null;
        try
        {
            opened = await Open(editors["mouse"], "mouse");
            AssertSingleItemMenu(opened);
            var plugin = Item(opened, "Fixture plugin action");
            plugin.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(invocations.Count == 1 && invocations[0].PaperId == PaperId &&
                    invocations[0].TodoId == "mouse" && invocations[0].Todo.Text == "Todo mouse",
                "the first-open plugin menu did not invoke the current Todo target");
            await Close();

            LifecycleInput.Focus(window, editors["mouse"]);
            editors["mouse"].Select(editors["mouse"].Text.Length, 0);
            LifecycleInput.Text("!");
            await Until(() => editors["mouse"].Text == "Todo mouse!", "editing after the first menu close");
            LifecycleInput.KeyChord(0x11, 0x5A);
            await Until(() => editors["mouse"].Text == "Todo mouse", "native text undo after the first menu close");

            opened = await Open(editors["keyboard"], "shift-f10");
            AssertSingleItemMenu(opened);
            await Close();
            opened = await Open(editors["apps"], "apps");
            AssertSingleItemMenu(opened);
            await Close();

            var row = Row("mouse");
            opened = await Open(row, "mouse", new Point(1, row.ActualHeight / 2));
            AssertSingleItemMenu(opened);
            await Close();
            opened = await Open(((Grid)row.Child).Children.OfType<CheckBox>().Single(), "mouse");
            AssertSingleItemMenu(opened);
            await Close();

            var handle = Descendants(row).OfType<Border>()
                .Single(border => border.Cursor == Cursors.SizeAll);
            opened = await Open((FrameworkElement)handle.Child, "mouse");
            Require(ReferenceEquals(handle.ContextMenu, opened) &&
                    opened.Items.OfType<MenuItem>().Count(item => Header(item) == "Fixture plugin action") == 1,
                "a drag-handle child glyph opened or decorated the row's different menu");
            await Close();

            foreach (var id in new[] { "path", "paper" })
            {
                var link = ((Grid)Row(id).Child).Children.OfType<Border>()
                    .Single(border => Grid.GetColumn(border) == 2);
                opened = await Open(link, "mouse");
                Item(opened, Strings.Get(id == "path" ? "MenuUnlinkPath" : "MenuUnlinkPaper"));
                if (id == "path") Item(opened, Strings.Get("MenuOpenLinkedPathLocation"));
                await Close();
            }

            foreach (var id in new[] { "mouse", "reminder" })
            {
                var reminderHost = ((Grid)Row(id).Child).Children.OfType<Grid>()
                    .Single(grid => Grid.GetColumn(grid) == 3);
                var reminder = reminderHost.Children.OfType<Border>()
                    .Single(border => border.Visibility == Visibility.Visible);
                opened = await Open(reminder, "mouse");
                var submenu = Item(opened, Strings.Get("TodoReminderSet"));
                Require(submenu.IsEnabled, "an unfinished item's reminder menu was disabled");
                submenu.IsSubmenuOpen = true;
                await Until(() => submenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled),
                    "reminder submenu population");
                submenu.IsSubmenuOpen = false;
                await Close();
            }
            opened = await Open(editors["done"], "apps");
            Require(!Item(opened, Strings.Get("TodoReminderSet")).IsEnabled,
                "a completed item's reminder menu was enabled");
            await Close();

            Select("mouse", "keyboard");
            opened = await Open(editors["mouse"], "shift-f10");
            Require(selected.SetEquals(["mouse", "keyboard"]), "keyboard first-open lost the selected group");
            Item(opened, Strings.Get("MenuCopySelectedTodos"));
            var translated = Item(opened, CopyTranslationStrings.Get("MenuCopyAsMarkdown"));
            Require(opened.Items.OfType<MenuItem>().Count(item => Header(item) == Header(translated)) == 1,
                "translated copy was duplicated during lazy construction");
            Require(opened.Items.OfType<MenuItem>().Count(item => Header(item) == "Fixture plugin action") == 1,
                "multi-selection first-open lost or duplicated the plugin action");
            translated.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(Clipboard.GetText() == TodoClipboardFormatter.ToMarkdown(
                    controller.State.Papers.Single(paper => paper.Id == PaperId).Items.Take(2)
                        .Select(item => (item.Text, item.Done))),
                "translated copy did not use the selected current Todos");
            await Close();

            opened = await Open(editors["apps"], "apps");
            Require(selected.Count == 0, "keyboard context menu outside the group retained stale selection");
            AssertSingleItemMenu(opened);
            await Close();

            Select("mouse", "keyboard");
            opened = await Open((FrameworkElement)handle.Child, "mouse");
            var complete = Item(opened, Strings.Get("MenuCompleteSelectedTodos"));
            await Close();
            complete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Flush();
            Require(controller.State.Papers.Single(paper => paper.Id == PaperId).Items
                    .Where(item => item.Id is "mouse" or "keyboard").All(item => item.Done),
                "a lazily constructed selection action did not update both live Todos");
            Console.WriteLine("PASS Todo lazy menus: native mouse, Shift+F10, Apps, child owners, selection, reminders, links, plugin and translated copy");
        }
        finally
        {
            if (opened != null) opened.IsOpen = false;
            selected.Clear();
        }

        Border Row(string id) => rows.Single(row => row.Tag as string == id);
        void Select(params string[] ids)
        {
            selected.Clear();
            selected.UnionWith(ids);
            foreach (var editor in editors.Values) editor.Select(0, 0);
            typeof(PaperWindow).GetMethod("ApplyTodoSelectionVisuals", Private)!.Invoke(window, null);
        }
        async Task<ContextMenu> Open(FrameworkElement target, string input, Point? point = null)
        {
            var owner = ContextMenuOwner(target);
            Require(owner != null, "context-menu test target had no owner");
            if (input != "mouse") LifecycleInput.Focus(window, target);
            else LifecycleInput.Focus(window, null);
            await Flush();
            var requested = owner!.ContextMenu!;
            textMenuOwner = owner as TodoTextBox;
            if (input == "mouse")
            {
                var localPoint = point ?? new Point(target.ActualWidth / 2, target.ActualHeight / 2);
                var screenPoint = target.PointToScreen(localPoint);
                // Startup first shows a transparent HWND. Wait until the requested
                // screen point belongs to this paper before sending any mouse input.
                await Until(() => LifecycleInput.IsMouseTarget(window, screenPoint),
                    "native context-menu target window is ready");
                // Inject actual movement, then let WPF establish the target before
                // injecting buttons. Moving the cursor alone can leave DirectlyOver null.
                LifecycleInput.MoveCursor(screenPoint);
                await Until(() => ReferenceEquals(ContextMenuOwner(Mouse.DirectlyOver as DependencyObject), owner),
                    "native cursor reaches the requested context-menu owner");
                LifecycleInput.RightClick();
            }
            else if (input == "shift-f10") LifecycleInput.KeyChord(0x10, 0x79);
            else LifecycleInput.KeyChord(0x5D);
            await Until(() => owner.ContextMenu?.IsOpen == true, input + " first context-menu opening");
            var menu = owner!.ContextMenu!;
            await Flush();
            Require(ReferenceEquals(menu, requested), input + " replaced the menu after WPF installed its close handler");
            if (textMenuOwner != null)
                Require(TextEditorMenuIsOpen(textMenuOwner), input + " bypassed WPF's text-editor context-menu state");
            Require(menu.Items.Count > 0, input + " displayed the empty placeholder");
            return menu;
        }
        async Task Close()
        {
            if (opened == null) return;
            if (opened.IsOpen)
            {
                LifecycleInput.KeyChord(0x1B);
                await Until(() => !opened.IsOpen, "native context-menu close");
            }
            opened = null;
            await Flush();
            if (textMenuOwner != null)
                Require(!TextEditorMenuIsOpen(textMenuOwner), "the closed menu stranded WPF's text-editor context-menu state");
            textMenuOwner = null;
        }
    }

    internal static async Task RunBindings(
        AppController controller,
        IReadOnlyDictionary<string, PaperWindow> windows)
    {
        var todo = controller.State.Papers.Single(paper => paper.Id == PaperId);
        var note = controller.State.Papers.Single(paper => paper.Id == LinkedPaperId);
        var todoWindow = windows[PaperId];
        Require(todoWindow.LinkPaperToTodo("apps", LinkedPaperId), "second link could not be prepared");
        controller.OpenLinkedPaper(LinkedPaperId, todoWindow);
        await Until(() => windows.TryGetValue(LinkedPaperId, out var opened) && opened.IsVisible,
            "linked note opens for association actions");
        var noteWindow = windows[LinkedPaperId];
        var button = (Button)Part(noteWindow, "_windowBindingButton");
        await Flush();

        controller.HidePaper(todo);
        var menu = await OpenBindingMenu(noteWindow, button);
        Require(Header((MenuItem)menu.Items[0]) == Strings.Get("MenuCancelBinding") &&
                Header((MenuItem)menu.Items[1]) == Strings.Get("MenuOpenBindingTodo"),
            "paper binding actions were missing, mislabeled, or out of order");
        InvokeMenuAction(menu, 1);
        await Until(() => todoWindow.IsVisible && todo.IsVisible && !todo.IsCollapsed,
            "binding menu opens its hidden Todo target");

        await PointAtBindingButton(noteWindow, button);
        LifecycleInput.DoubleClick();
        await Until(() => todo.Items.All(item => item.LinkedPaperId != LinkedPaperId),
            "double click removes every link to the current note");
        var editors = (Dictionary<string, TodoTextBox>)Part(todoWindow, "_todoEditors");
        LifecycleInput.Focus(todoWindow, editors["paper"]);
        LifecycleInput.KeyChord(0x11, 0x5A);
        await Until(() => todo.Items.Count(item => item.LinkedPaperId == LinkedPaperId) == 2,
            "one undo restores both links removed by the association button");
        menu = await OpenBindingMenu(noteWindow, button);
        InvokeMenuAction(menu, 0);
        Require(todo.Items.All(item => item.LinkedPaperId != LinkedPaperId),
            "the first context action did not remove the paper associations");

        var executable = Path.Combine(AppContext.BaseDirectory, "PaperTodo.BindingTarget.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "PaperTodo.LifecycleChecks.exe"), executable);
        using var targetProcess = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })!;
        Process? reopenedProcess = null;
        try
        {
            ExternalWindowSnapshot target = default;
            await Until(() => ExternalWindowNative.TryFindProgramWindow(
                executable, "PaperTodo binding fixture", out target) && target.IsUsableTarget,
                "isolated app target becomes visible");
            LifecycleInput.Focus(noteWindow, null);
            var attach = typeof(PaperWindow).GetMethods(Private).Single(method =>
                method.Name == "AttachExperimentalWindowTether" && method.GetParameters().Length == 1);
            Require((bool)attach.Invoke(noteWindow, [target.Identity])!,
                "binding to the real external process failed");
            menu = await OpenBindingMenu(noteWindow, button);
            Require(Header((MenuItem)menu.Items[1]) == Strings.Get("MenuOpenBindingProgram"),
                "window binding did not offer the app action in second position");
            InvokeMenuAction(menu, 1);
            await Until(() => ExternalWindowNative.ForegroundWindow == target.Identity.Handle,
                "opening a running app brings its existing window to the foreground");

            _ = PostMessage(target.Identity.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            await Until(() => targetProcess.HasExited, "bound app closes normally");
            await Until(() => noteWindow.IsVisible && noteWindow.HasExperimentalExternalWindowAttachment,
                "closed app leaves a visible paper with its binding intact");
            controller.HidePaper(note);
            controller.ShowPaper(note);
            await Until(() => noteWindow.IsVisible && noteWindow.Opacity > .99,
                "manual show of a paper whose target app is closed");
            Require(noteWindow.HasExperimentalExternalWindowAttachment,
                "manual show removed the retained closed-app binding");

            controller.State.UseCapsuleMode = true;
            noteWindow.SetCollapsedState(true, animate: false, saveGeometry: false);
            Require(note.IsCollapsed && noteWindow.HasExperimentalExternalWindowAttachment,
                "collapsing a closed-app binding removed the association");
            noteWindow.SetCollapsedState(false, animate: false, saveGeometry: false);
            Require(!note.IsCollapsed && noteWindow.HasExperimentalExternalWindowAttachment,
                "expanding a closed-app binding removed the association");

            menu = await OpenBindingMenu(noteWindow, button);
            InvokeMenuAction(menu, 1);
            await Until(() => ExternalWindowNative.TryFindProgramWindow(
                executable, "PaperTodo binding fixture", out target) && target.IsUsableTarget,
                "opening a closed app starts its executable");
            reopenedProcess = Process.GetProcessById((int)target.Identity.ProcessId);
            await Until(() => noteWindow.TracksExperimentalExternalWindow(target.Identity.Handle),
                "the retained binding automatically reconnects to the restarted app");
            var before = noteWindow.Left;
            Require(SetWindowPos(target.Identity.Handle, IntPtr.Zero,
                target.Bounds.Left + 40, target.Bounds.Top, 0, 0, 0x0015), "target move failed");
            await Until(() => Math.Abs(noteWindow.Left - before) > 10,
                "reopened target continues moving its bound paper");

            _ = ShowWindow(target.Identity.Handle, 6); // SW_MINIMIZE
            await Until(() => ExternalWindowNative.TryGetSnapshot(target.Identity, out var minimized) &&
                minimized.IsMinimized, "bound target minimizes");
            typeof(PaperWindow).GetMethod("OpenWindowBindingTarget", Private)!.Invoke(noteWindow, null);
            await Until(() => ExternalWindowNative.TryGetSnapshot(target.Identity, out var restored) &&
                restored.IsUsableTarget && noteWindow.IsVisible,
                "open-app action restores a minimized target without clearing the binding");
            Require(noteWindow.HasExperimentalExternalWindowAttachment,
                "restoring the minimized app cleared its binding");
            await PointAtBindingButton(noteWindow, button);
            LifecycleInput.DoubleClick();
            await Until(() => !noteWindow.HasExperimentalExternalWindowAttachment,
                "double click also cancels a window binding");
            Require(!reopenedProcess.HasExited, "unbinding closed the target application");
            Console.WriteLine("PASS paper bindings: menu order, target open, native double click, batch unlink undo, app close/reopen, form changes and restored window tracking");
        }
        finally
        {
            if (button.ContextMenu != null) button.ContextMenu.IsOpen = false;
            foreach (var process in Process.GetProcessesByName("PaperTodo.BindingTarget"))
            {
                using (process)
                {
                    if (!process.HasExited && string.Equals(
                        ExternalWindowNative.GetExecutablePath((uint)process.Id), executable,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit();
                    }
                }
            }
            reopenedProcess?.Dispose();
        }
    }

    private static async Task PointAtBindingButton(PaperWindow window, Button button)
    {
        LifecycleInput.Focus(window, null);
        await Flush();
        Require(button.IsVisible, "association button is not reachable");
        var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        await Until(() => LifecycleInput.IsMouseTarget(window, point), "binding button HWND is ready");
        LifecycleInput.MoveCursor(point);
        await Until(() => button.IsMouseOver, "native cursor reaches the association button");
    }

    private static async Task<ContextMenu> OpenBindingMenu(PaperWindow window, Button button)
    {
        await PointAtBindingButton(window, button);
        LifecycleInput.RightClick();
        await Until(() => button.ContextMenu?.IsOpen == true, "native binding context menu opens");
        return button.ContextMenu!;
    }

    private static void InvokeMenuAction(ContextMenu menu, int index)
    {
        var action = (MenuItem)menu.Items[index];
        menu.IsOpen = false;
        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private static void AssertSingleItemMenu(ContextMenu menu)
    {
        Item(menu, Strings.Get("MenuTodoItem"));
        Item(menu, Strings.Get("MenuDeleteItem"));
    }

    private static MenuItem Item(ContextMenu menu, string header) =>
        menu.Items.OfType<MenuItem>().SingleOrDefault(item => Header(item) == header) ??
        throw new InvalidOperationException("Missing context menu action: " + header);

    private static string? Header(MenuItem item) => item.Header is TextBlock text ? text.Text : item.Header as string;
    private static bool TextEditorMenuIsOpen(TodoTextBox textBox)
    {
        var editor = typeof(TextBoxBase).GetProperty("TextEditor", Private)!.GetValue(textBox)!;
        return (bool)editor.GetType().GetProperty("IsContextMenuOpen", Private)!.GetValue(editor)!;
    }
    private static object Part(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static FrameworkElement? ContextMenuOwner(DependencyObject? target)
    {
        while (target != null)
        {
            if (target is FrameworkElement { ContextMenu: not null } owner) return owner;
            target = target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target);
        }
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static async Task Flush() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);

    private static async Task Until(Func<bool> ready, string name)
    {
        var started = Stopwatch.GetTimestamp();
        while (!ready())
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 4) throw new TimeoutException(name);
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal static class LifecycleInput
{
    internal static void Focus(PaperWindow window, IInputElement? element)
    {
        window.Activate();
        SetForegroundWindow(new WindowInteropHelper(window).Handle);
        if (element != null) Keyboard.Focus(element);
    }

    internal static void KeyChord(params ushort[] keys)
    {
        var input = keys.Select(key => Key(key, up: false))
            .Concat(keys.Reverse().Select(key => Key(key, up: true))).ToArray();
        Send(input);
    }

    internal static void MoveCursor(Point point)
    {
        var left = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        var top = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        var width = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
        var height = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
        if (width <= 0 || height <= 0) throw new InvalidOperationException("No virtual desktop for native input.");
        // As in WPF's UIAutomation input provider, target the center of the desired
        // pixel's normalized range so truncation cannot move into an adjacent pixel.
        var x = (int)Math.Clamp((Math.Round(point.X) - left + 0.5) * 65536 / width, 0, 65535);
        var y = (int)Math.Clamp((Math.Round(point.Y) - top + 0.5) * 65536 / height, 0, 65535);
        Send([new Input { Type = 0, Data = new InputData { Mouse = new MouseInput
        {
            X = x, Y = y, Flags = 0x0001 | 0x8000 | 0x4000 // MOVE | ABSOLUTE | VIRTUALDESK
        } } }]);
    }

    internal static bool IsMouseTarget(PaperWindow window, Point point) =>
        WindowFromPoint(new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) }) ==
        new WindowInteropHelper(window).Handle;

    internal static void RightClick()
    {
        Send([new Input { Type = 0, Data = new InputData { Mouse = new MouseInput { Flags = 0x0008 } } },
            new Input { Type = 0, Data = new InputData { Mouse = new MouseInput { Flags = 0x0010 } } }]);
    }

    internal static void DoubleClick()
    {
        var click = new[]
        {
            new Input { Type = 0, Data = new InputData { Mouse = new MouseInput { Flags = 0x0002 } } },
            new Input { Type = 0, Data = new InputData { Mouse = new MouseInput { Flags = 0x0004 } } }
        };
        Send(click.Concat(click).ToArray());
    }

    internal static void Text(string value) => Send(value.SelectMany(character => new[]
    {
        new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { ScanCode = character, Flags = 0x0004 } } },
        new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { ScanCode = character, Flags = 0x0006 } } }
    }).ToArray());

    private static Input Key(ushort key, bool up) => new()
    {
        Type = 1,
        Data = new InputData { Keyboard = new KeyboardInput
        {
            VirtualKey = key, Flags = (up ? 0x0002u : 0u) | (key == 0x5D ? 0x0001u : 0u)
        } }
    };

    private static void Send(Input[] input)
    {
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length)
            throw new InvalidOperationException("Native fixture input injection failed: " + Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
}
