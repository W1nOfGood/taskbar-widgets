using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TaskbarWidgets
{
    /// <summary>
    /// Minimal raw-COM UI Automation: reads where the Windows 11 taskbar buttons are (Start, search,
    /// Task View, widgets, every app icon...). Using the native API directly avoids loading the managed
    /// UIA + WPF assemblies (~10+ MB RAM). Unused vtable slots are placeholders that keep the order right.
    /// </summary>
    internal static class Uia
    {
        [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] class CUIAutomation { }

        [Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IUIAutomation
        {
            [PreserveSig] int CompareElements(); [PreserveSig] int CompareRuntimeIds(); [PreserveSig] int GetRootElement();
            [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
            [PreserveSig] int ElementFromPoint(); [PreserveSig] int GetFocusedElement(); [PreserveSig] int GetRootElementBuildCache();
            [PreserveSig] int ElementFromHandleBuildCache(); [PreserveSig] int ElementFromPointBuildCache(); [PreserveSig] int GetFocusedElementBuildCache();
            [PreserveSig] int CreateTreeWalker(); [PreserveSig] int get_ControlViewWalker(); [PreserveSig] int get_ContentViewWalker();
            [PreserveSig] int get_RawViewWalker(); [PreserveSig] int get_RawViewCondition(); [PreserveSig] int get_ControlViewCondition();
            [PreserveSig] int get_ContentViewCondition(); [PreserveSig] int CreateCacheRequest();
            [PreserveSig] int CreateTrueCondition([MarshalAs(UnmanagedType.IUnknown)] out object condition);
            [PreserveSig] int CreateFalseCondition();
            [PreserveSig] int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, [MarshalAs(UnmanagedType.IUnknown)] out object condition);
        }

        [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IUIAutomationElement
        {
            [PreserveSig] int SetFocus(); [PreserveSig] int GetRuntimeId();
            [PreserveSig] int FindFirst(int scope, [MarshalAs(UnmanagedType.IUnknown)] object condition, out IUIAutomationElement found);
            [PreserveSig] int FindAll(int scope, [MarshalAs(UnmanagedType.IUnknown)] object condition, out IUIAutomationElementArray found);
            [PreserveSig] int FindFirstBuildCache(); [PreserveSig] int FindAllBuildCache(); [PreserveSig] int BuildUpdatedCache();
            [PreserveSig] int GetCurrentPropertyValue(); [PreserveSig] int GetCurrentPropertyValueEx(); [PreserveSig] int GetCachedPropertyValue();
            [PreserveSig] int GetCachedPropertyValueEx(); [PreserveSig] int GetCurrentPatternAs(); [PreserveSig] int GetCachedPatternAs();
            [PreserveSig] int GetCurrentPattern(); [PreserveSig] int GetCachedPattern(); [PreserveSig] int GetCachedParent(); [PreserveSig] int GetCachedChildren();
            [PreserveSig] int get_CurrentProcessId(); [PreserveSig] int get_CurrentControlType(); [PreserveSig] int get_CurrentLocalizedControlType();
            [PreserveSig] int get_CurrentName(); [PreserveSig] int get_CurrentAcceleratorKey(); [PreserveSig] int get_CurrentAccessKey();
            [PreserveSig] int get_CurrentHasKeyboardFocus(); [PreserveSig] int get_CurrentIsKeyboardFocusable(); [PreserveSig] int get_CurrentIsEnabled();
            [PreserveSig] int get_CurrentAutomationId([MarshalAs(UnmanagedType.BStr)] out string id);
            [PreserveSig] int get_CurrentClassName([MarshalAs(UnmanagedType.BStr)] out string cls);
            [PreserveSig] int get_CurrentHelpText();
            [PreserveSig] int get_CurrentCulture(); [PreserveSig] int get_CurrentIsControlElement(); [PreserveSig] int get_CurrentIsContentElement();
            [PreserveSig] int get_CurrentIsPassword(); [PreserveSig] int get_CurrentNativeWindowHandle(); [PreserveSig] int get_CurrentItemType();
            [PreserveSig] int get_CurrentIsOffscreen(); [PreserveSig] int get_CurrentOrientation(); [PreserveSig] int get_CurrentFrameworkId();
            [PreserveSig] int get_CurrentIsRequiredForForm(); [PreserveSig] int get_CurrentItemStatus();
            [PreserveSig] int get_CurrentBoundingRectangle(out Native.RECT r);
        }

        [Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IUIAutomationElementArray
        {
            [PreserveSig] int get_Length(out int length);
            [PreserveSig] int GetElement(int index, out IUIAutomationElement element);
        }

        const int UIA_AutomationIdPropertyId = 30011, TreeScope_Children = 2, TreeScope_Descendants = 4;
        static IUIAutomation automation;
        static object trueCondition, frameCondition, startCondition;

        static void Init()
        {
            if (automation != null) return;
            automation = (IUIAutomation)new CUIAutomation();
            automation.CreateTrueCondition(out trueCondition);
            automation.CreatePropertyCondition(UIA_AutomationIdPropertyId, "TaskbarFrame", out frameCondition);
            automation.CreatePropertyCondition(UIA_AutomationIdPropertyId, "StartButton", out startCondition);
        }

        /// <summary>Result of one scan of a taskbar's buttons (screen pixels).</summary>
        internal sealed class Scan
        {
            public Rectangle Start = Rectangle.Empty;
            public readonly List<Rectangle> Buttons = new List<Rectangle>(); // everything that occupies the taskbar
            public readonly List<Rectangle> Tray = new List<Rectangle>();    // system tray part only
            public bool Ok;
        }

        /// <summary>
        /// Reads every button on the taskbar (the children of the XAML "TaskbarFrame"). Only works while
        /// the taskbar is shown; with auto-hide the XAML tree is hidden, so callers keep the last result.
        /// </summary>
        public static Scan ScanTaskbar(IntPtr taskbar)
        {
            Init();
            var res = new Scan();
            Native.RECT tbr;
            if (!Native.GetWindowRect(taskbar, out tbr)) return res;
            int tbw = tbr.Right - tbr.Left;

            // Start from the XAML island's input site: walking from the taskbar HWND itself would also walk
            // into our own widget windows (they are owned by the taskbar) and ping our UI thread.
            IntPtr bridge = Native.FindWindowEx(taskbar, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null);
            IntPtr site = bridge == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(bridge, IntPtr.Zero, "Windows.UI.Input.InputSite.WindowClass", null);
            if (site == IntPtr.Zero) site = bridge;
            if (site == IntPtr.Zero) return res;

            IUIAutomationElement root = null;
            try
            {
                if (automation.ElementFromHandle(site, out root) != 0 || root == null) return res;
                // Children of the input site: the TaskbarFrame (Start, search, Task View, widgets, apps...)
                // plus the system tray buttons (hidden icons chevron, network, volume, clock, show desktop).
                Collect(root, res, tbw, true);
                res.Ok = res.Buttons.Count > 0;
                return res;
            }
            finally
            {
                if (root != null) Marshal.ReleaseComObject(root);
            }
        }

        static void Collect(IUIAutomationElement parent, Scan res, int taskbarWidth, bool descendIntoFrame)
        {
            IUIAutomationElementArray kids = null;
            try
            {
                if (parent.FindAll(TreeScope_Children, trueCondition, out kids) != 0 || kids == null) return;
                int n; kids.get_Length(out n);
                for (int i = 0; i < n; i++)
                {
                    IUIAutomationElement el;
                    if (kids.GetElement(i, out el) != 0 || el == null) continue;
                    try
                    {
                        string id; el.get_CurrentAutomationId(out id);
                        if (descendIntoFrame && id == "TaskbarFrame") { Collect(el, res, taskbarWidth, false); continue; }
                        Native.RECT r;
                        if (el.get_CurrentBoundingRectangle(out r) != 0) continue;
                        var rc = r.ToRectangle();
                        if (rc.Width <= 0 || rc.Height <= 0 || rc.Width > taskbarWidth * 0.8) continue; // skip full-width panes
                        if (id == "StartButton") res.Start = rc;
                        if (descendIntoFrame) res.Tray.Add(rc); // top level = system tray buttons
                        res.Buttons.Add(rc);
                    }
                    finally { Marshal.ReleaseComObject(el); }
                }
            }
            finally { if (kids != null) Marshal.ReleaseComObject(kids); }
        }
    }
}
