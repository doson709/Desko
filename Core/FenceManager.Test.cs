using System;
using System.Linq;
using Desko.Interop;
using Desko.Models;
using Desko.Shell;
using Desko.UI;

namespace Desko.Core
{
    internal sealed partial class FenceManager
    {
        // Commands from the test channel (only when DESKO_TEST_PIPE is set; see TestChannel). Fences are named
        // by the title of any of their tabs; points and sizes are screen pixels.
        public string RunTestCommand(string line)
        {
            var a = line.Split(' ', 2);
            string cmd = a[0], rest = a.Length > 1 ? a[1] : "";
            FenceWindow Window(string title) =>
                _windows.FirstOrDefault(w => TabsOf(w.Config).Any(t => t.Title == title))
                ?? throw new ArgumentException("no fence " + title);
            string[] Args(int n) => rest.Split(' ', n);

            switch (cmd)
            {
                case "state":
                {
                    var prefix = rest.Length > 0 ? rest : "Desko test";
                    var state = new
                    {
                        Hidden,
                        Config.IconsParked,
                        Cell = new[] { IconGrid.Cell.X, IconGrid.Cell.Y, IconGrid.Offset.X, IconGrid.Offset.Y },
                        Zones = _hook.Zones.Length,
                        Windows = _windows.Select(w => new
                        {
                            w.Config.Title,
                            Tabs = TabsOf(w.Config).Select(t => t.Title).ToArray(),
                            Rect = Rect(w.ScreenRect),
                            Resting = Rect(w.RestingRect),
                            Body = Rect(w.BodyRect),
                            w.Config.RolledUp,
                            w.ShowsIcons,
                            w.ScrollRow,
                            w.DragModeOn,
                            Order = w.IconOrder,
                            Members = w.Config.Members,
                            Cell = new[] { w.CellWidth, w.CellHeight },
                            w.ProxyCount,
                        }),
                        Placed = _placed.Where(p => p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                        .ToDictionary(p => p.Key, p => new[] { p.Value.X, p.Value.Y }),
                    };
                    return System.Text.Json.JsonSerializer.Serialize(state);
                }
                case "sync": // "sync" after file changes; "sync user dx dy" after the user dragged icons that far
                {
                    var p = rest.Split(' ');
                    if (p[0] == "user") NoteDrag(new Native.POINT(0, 0), new Native.POINT(int.Parse(p[1]), int.Parse(p[2])));
                    SyncDesktop(user: p[0] == "user");
                    return "ok";
                }
                case "press":
                {
                    var p = Args(2);
                    _lastDesktopPress = (DateTime.UtcNow, new Native.POINT(int.Parse(p[0]), int.Parse(p[1])));
                    return "ok";
                }
                case "scroll":
                {
                    int cut = rest.LastIndexOf(' ');
                    Window(rest[..cut]).ScrollRows(int.Parse(rest[(cut + 1)..]));
                    return "ok";
                }
                case "tab":
                {
                    var p = rest.Split('|');
                    var w = Window(p[0]);
                    w.SwitchTab(TabsOf(w.Config).First(t => t.Title == p[1]));
                    return "ok";
                }
                case "rollup":
                    Window(rest).ToggleRollUp();
                    return "ok";
                case "peek":
                {
                    var p = rest.Split('|');
                    Window(p[0]).TestPeek(p[1] == "on");
                    return "ok";
                }
                case "bounds":
                {
                    var p = rest.Split('|');
                    var n = p[1].Split(' ').Select(int.Parse).ToArray();
                    var w = Window(p[0]);
                    w.Config.X = n[0]; w.Config.Y = n[1]; w.Config.Width = n[2]; w.Config.Height = n[3];
                    SyncGroup(w.Config);
                    w.ApplyBounds();
                    SaveSoon();
                    FenceMoveEnded();
                    return "ok";
                }
                case "hide":
                    SetHidden(true);
                    return "ok";
                case "show":
                    SetHidden(false);
                    return "ok";
                case "dragmode":
                {
                    var p = rest.Split('|');
                    Window(p[0]).SetDragMode(p[1] == "on");
                    return "ok";
                }
                case "drop": // title|x y|name1;name2 — what a drop of desktop icons onto the fence does
                {
                    var p = rest.Split('|');
                    var xy = p[1].Split(' ').Select(int.Parse).ToArray();
                    var w = Window(p[0]);
                    Checkpoint($"Đưa mục vào \"{w.Config.Title}\"");
                    AddMembers(w.Config, p[2].Split(';'), new Native.POINT(xy[0], xy[1]));
                    return "ok";
                }
                case "import": // title|x y|copy or move|path1;path2
                {
                    var p = rest.Split('|');
                    var xy = p[1].Split(' ').Select(int.Parse).ToArray();
                    var w = Window(p[0]);
                    ImportToDesktop(w.Config, p[3].Split(';'), p[2] == "copy", new Native.POINT(xy[0], xy[1]), w.Handle);
                    return "ok";
                }
                case "dump":
                    foreach (var w in _windows) w.DumpVisuals(rest);
                    return "ok";
                case "undo":
                    Undo();
                    return "ok";
                case "confirm":
                    Dialogs.TestAnswer = rest == "yes" ? true : rest == "no" ? false : null;
                    return "ok";
                case "remove": // title|whole or tab
                {
                    var p = rest.Split('|');
                    RemoveFence(Window(p[0]), p[1] == "whole");
                    return "ok";
                }
                case "addtab":
                    AddTab(Window(rest));
                    return "ok";
                case "rename": // title|new title
                {
                    var p = rest.Split('|');
                    RenameFence(Window(p[0]), p[1]);
                    return "ok";
                }
                case "merge":
                {
                    var p = rest.Split('|');
                    MergeInto(Window(p[0]), Window(p[1]));
                    return "ok";
                }
                case "detach":
                    DetachTab(Window(rest));
                    return "ok";
                case "portal":
                {
                    var p = rest.Split('|');
                    SetPortal(Window(p[0]), p[1]);
                    return "ok";
                }
                case "tidy":
                    return TidyDesktop(null, quiet: false).ToString();
                case "reveal":
                {
                    var p = rest.Split('|');
                    var w = Window(p[0]);
                    w.RevealItem(p[1]);
                    return "ok";
                }
                case "sort":
                {
                    var p = rest.Split('|');
                    var w = Window(p[0]);
                    w.Config.SortMode = Enum.Parse<SortMode>(p[1]);
                    w.Reload();
                    return "ok";
                }
                case "bench": // what one frame of dragging a fence costs: moving the window + its icons, repainting its backdrop
                {
                    var w = Window(rest);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int x = w.Config.X;
                    for (int i = 0; i < 30; i++)
                    {
                        w.Config.X = x + (i % 2 == 0 ? 6 : 0);
                        w.ApplyBounds();
                        LayoutWindow(w);
                    }
                    long move = sw.ElapsedMilliseconds;
                    w.Config.X = x;
                    w.ApplyBounds();
                    LayoutWindow(w);
                    return $"window + icons: {move / 30.0:0.0} ms per frame; backdrop paint: {w.BenchBackdrop(30):0.0} ms per frame";
                }
                case "lift": // title|ready, on or off: what hovering the title, then dragging / resizing a fence does to its icons
                {
                    var p = rest.Split('|');
                    Window(p[0]).TestLift(p[1]);
                    return "ok";
                }
                case "autoarrange":
                    Config.AutoArrangeLoose = rest == "on";
                    LayoutAll();
                    return "ok";
                case "arrange":
                    ArrangeLooseIcons();
                    return "ok";
                case "backdrop":
                    Config.Backdrop = rest;
                    ApplySettings();
                    return "ok";
                case "memory":
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    return $"{System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} MB private, {GC.GetTotalMemory(true) / 1048576} MB managed";
                case "select": // all or none
                    DesktopIcons.SelectAll(rest == "all");
                    return DesktopIcons.SelectedCount().ToString();
                case "time": // how long a full look at the desktop takes
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var icons = DesktopIcons.Enumerate();
                    long enumerate = sw.ElapsedMilliseconds;
                    sw.Restart();
                    SyncDesktop(user: false);
                    return $"enumerate {icons?.Count} icons: {enumerate} ms; sync: {sw.ElapsedMilliseconds} ms";
                }
                default:
                    return "unknown command " + cmd;
            }
        }

        static int[] Rect(Native.RECT r) => new[] { r.Left, r.Top, r.Right, r.Bottom };
    }
}
