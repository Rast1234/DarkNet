﻿using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Dark.Net;

/// <summary>
/// <para>Implementation of the DarkNet library. Used for making title bars and context menus of your windows dark in Windows 10 1809 and later.</para>
/// <para>Usage:</para>
/// <list type="number">
/// <item><description>Construct a new instance with <c>new DarkNet()</c>, or use the shared singleton <see cref="Instance"/>.</description></item>
/// <item><description>Optionally, call <see cref="SetCurrentProcessTheme"/> before showing any windows in your process, such as in a <see cref="System.Windows.Application.Startup"/> event handler for your WPF program, or at the beginning of <c>Main</c> in your Forms program.</description></item>
/// <item><description>Call <see cref="SetWindowThemeWpf"/> or <see cref="SetWindowThemeForms"/> for each window before you show it. For WPF, you should do this in <see cref="Window.SourceInitialized"/>. For Forms, you should do this after constructing the <see cref="Form"/> instance.</description></item>
/// </list>
/// </summary>
public class DarkNet: IDarkNet {

    private const string PersonalizeKey = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Lazy<DarkNet> LazyInstance = new(LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// <para>Shared singleton instance of the <see cref="DarkNet"/> class that you can use without constructing your own instance. Created lazily the first time it is accessed.</para>
    /// <para>You may want to construct your own instance using <c>new DarkNet()</c> in order to manage the memory lifecycle and dispose of it manually to insulate yourself from other consumers that may try to dispose of <see cref="Instance"/>.</para>
    /// </summary>
    public static IDarkNet Instance => LazyInstance.Value;

    protected readonly ConcurrentDictionary<IntPtr, Theme> PreferredWindowModes = new();

    private   bool?  _userDefaultAppThemeIsDark;
    private   bool?  _userTaskbarThemeIsDark;
    protected Theme? PreferredAppTheme;
    protected bool?  EffectiveProcessThemeIsDark;

    private volatile int _processThemeChanged; // int instead of bool to support Interlocked atomic operations

    /// <inheritdoc />
    public event EventHandler<bool>? UserDefaultAppThemeIsDarkChanged;

    /// <inheritdoc />
    public event EventHandler<bool>? UserTaskbarThemeIsDarkChanged;

    /// <inheritdoc />
    public event EventHandler<bool>? EffectiveCurrentProcessThemeIsDarkChanged;

    /// <summary>
    /// <para>Create a new instance of the DarkNet library class. Alternatively, you can use the static singleton <see cref="Instance"/>.</para>
    /// <para>Useful if you want to manage the memory lifecycle and dispose of it manually to insulate yourself from other consumers that may try to dispose of <see cref="Instance"/>.</para>
    /// </summary>
    public DarkNet() {
        try {
            SystemEvents.UserPreferenceChanged += OnSettingsChanged;
        } catch (ExternalException) { }
    }

    /// <inheritdoc />
    public virtual void SetCurrentProcessTheme(Theme theme) {
        _processThemeChanged = 1;

        try {
            // Windows 10 1903 and later
            Win32.SetPreferredAppMode(theme switch {
                Theme.Light => AppMode.Default,
                Theme.Auto  => AppMode.AllowDark,
                Theme.Dark  => AppMode.ForceDark,
                _           => AppMode.Default
            });
        } catch (Exception e1) when (e1 is not OutOfMemoryException) {
            try {
                // Windows 10 1809 only
                Win32.AllowDarkModeForApp(true);
            } catch (Exception e2) when (e2 is not OutOfMemoryException) {
                Trace.TraceWarning("Failed to set dark mode for process: {0}", e1.Message);
            }
        }

        PreferredAppTheme = theme;

        RefreshTitleBarThemeColor();

        if (EffectiveProcessThemeIsDark == null) {
            EffectiveCurrentProcessThemeIsDark = theme switch {
                Theme.Auto  => UserDefaultAppThemeIsDark && !IsHighContrast(),
                Theme.Light => false,
                Theme.Dark  => !IsHighContrast(),
                _           => false
            };
        }
    }

    /// <inheritdoc />
    // Not overloading one method to cover both WPF and Forms so that consumers don't have to add references to both PresentationFramework and System.Windows.Forms just to use one overloaded variant
    public virtual void SetWindowThemeWpf(Window window, Theme theme) {
        bool isWindowInitialized = PresentationSource.FromVisual(window) != null;
        if (!isWindowInitialized) {
            ImplicitlySetProcessThemeIfFirstCall(theme);
            window.SourceInitialized += OnSourceInitialized;

            void OnSourceInitialized(object? _, EventArgs eventArgs) {
                window.SourceInitialized -= OnSourceInitialized;
                SetWindowThemeWpf(window, theme);
            }
        } else {
            IntPtr windowHandle = new WindowInteropHelper(window).Handle;

            try {
                SetModeForWindow(windowHandle, theme);
            } catch (DarkNetException.LifecycleException) {
                throw new InvalidOperationException($"Called {nameof(SetWindowThemeWpf)}() too late, call it in OnSourceInitialized or the Window subclass's constructor");
            }

            void OnClosing(object _, CancelEventArgs args) {
                window.Closing -= OnClosing;
                OnWindowClosing(windowHandle);
            }

            window.Closing += OnClosing;
        }
    }

    /// <inheritdoc />
    public virtual void SetWindowThemeForms(Form window, Theme theme) {
        try {
            SetModeForWindow(window.Handle, theme);
        } catch (DarkNetException.LifecycleException) {
            throw new InvalidOperationException($"Called {nameof(SetWindowThemeForms)}() too late, call it before Form.Show() or Application.Run(), and after " +
                $"{nameof(IDarkNet)}.{nameof(SetCurrentProcessTheme)}()");
        }

        void OnClosing(object _, CancelEventArgs args) {
            window.Closing -= OnClosing;
            OnWindowClosing(window.Handle);
        }

        window.Closing += OnClosing;
    }

    /// <inheritdoc />
    public virtual void SetWindowThemeRaw(IntPtr windowHandle, Theme theme) {
        try {
            SetModeForWindow(windowHandle, theme);
        } catch (DarkNetException.LifecycleException) {
            throw new InvalidOperationException($"Called {nameof(SetWindowThemeRaw)}() too late, call it before the window is visible.");
        }
    }

    private void ImplicitlySetProcessThemeIfFirstCall(Theme theme) {
        if (_processThemeChanged == 0) {
            SetCurrentProcessTheme(theme);
        }
    }

    /// <summary>
    ///     <para>call this after creating but before showing a window, such as WPF's Window.OnSourceInitialized or Forms' Form.Load</para>
    ///     <para>if window.Visibility==VISIBLE and WindowPlacement.ShowCmd == SW_HIDE (or whatever), it was definitely called too early </para>
    ///     <para>if GetWindowInfo().style.WS_VISIBLE == true then it was called too late</para>
    /// </summary>
    /// <exception cref="DarkNetException.LifecycleException">if it is called too late</exception>
    protected virtual void SetModeForWindow(IntPtr windowHandle, Theme windowTheme) {
        ImplicitlySetProcessThemeIfFirstCall(windowTheme);

        bool isFirstRunForWindow = true;
        PreferredWindowModes.AddOrUpdate(windowHandle, windowTheme, (_, _) => {
            isFirstRunForWindow = false;
            return windowTheme;
        });

        if (isFirstRunForWindow && IsWindowVisible(windowHandle)) {
            throw new DarkNetException.LifecycleException("Called too late, call it before the window is visible.");
        }

        Win32.AllowDarkModeForWindow(windowHandle, windowTheme != Theme.Light);
        RefreshTitleBarThemeColor(windowHandle);
    }

    protected void OnWindowClosing(IntPtr windowHandle) {
        PreferredWindowModes.TryRemove(windowHandle, out _);
    }

    private void OnSettingsChanged(object sender, UserPreferenceChangedEventArgs args) {
        if (args.Category == UserPreferenceCategory.General && (_userDefaultAppThemeIsDark != UserDefaultAppThemeIsDark || _userTaskbarThemeIsDark != UserTaskbarThemeIsDark)) {
            RefreshTitleBarThemeColor();
        }
    }

    private void RefreshTitleBarThemeColor() {
        foreach (IntPtr trackedWindow in PreferredWindowModes.Keys) {
            RefreshTitleBarThemeColor(trackedWindow);
        }
    }

    protected virtual void RefreshTitleBarThemeColor(IntPtr windowHandle) {
        if (!PreferredWindowModes.TryGetValue(windowHandle, out Theme windowTheme)) {
            windowTheme = Theme.Auto;
        }

        Theme appTheme                  = PreferredAppTheme ?? Theme.Auto;
        bool  userDefaultAppThemeIsDark = UserDefaultAppThemeIsDark;
        bool  isHighContrast            = IsHighContrast();

        if (appTheme == Theme.Auto) {
            appTheme = userDefaultAppThemeIsDark ? Theme.Dark : Theme.Light;
        }

        if (windowTheme == Theme.Auto) {
            windowTheme = appTheme;
        }

        if (isHighContrast) {
            windowTheme = Theme.Light;
            appTheme    = Theme.Light;
        } else if (!Win32.IsDarkModeAllowedForWindow(windowHandle)) {
            windowTheme = Theme.Light;
        }

        EffectiveCurrentProcessThemeIsDark = appTheme == Theme.Dark;

        bool   isDarkTheme              = windowTheme == Theme.Dark;
        int    attributeValueBufferSize = Marshal.SizeOf(typeof(bool));
        IntPtr attributeValueBuffer     = Marshal.AllocHGlobal(attributeValueBufferSize);
        Marshal.WriteInt32(attributeValueBuffer, Convert.ToInt32(isDarkTheme));

        try {
            // Windows 10 1903 and later
            WindowCompositionAttributeData windowCompositionAttributeData = new(WindowCompositionAttribute.WcaUsedarkmodecolors, attributeValueBuffer, attributeValueBufferSize);
            Win32.SetWindowCompositionAttribute(windowHandle, ref windowCompositionAttributeData);
        } catch (Exception e1) when (e1 is not OutOfMemoryException) {
            try {
                // Windows 10 1809 only
                Win32.SetProp(windowHandle, "UseImmersiveDarkModeColors", new IntPtr(Convert.ToInt64(isDarkTheme)));
            } catch (Exception e2) when (e2 is not OutOfMemoryException) {
                Trace.TraceWarning("Failed to set dark mode for window: {0}", e1.Message);
            }
        } finally {
            Marshal.FreeHGlobal(attributeValueBuffer);
        }

        /*
         * Needed for subsequent (after the window has already been shown) theme changes, otherwise the title bar will only update after you later hide, blur, or resize the window.
         * Not needed when changing the theme for the first time, before the window has ever been shown.
         * Windows 11 does not need this.
         * Windows 10 needs this (1809, 22H2, and likely every other version).
         * Neither RedrawWindow() nor UpdateWindow() fix this.
         * https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-ncactivate
         */
        const uint activateNonClientArea = 0x86;
        bool       isWindowActive        = windowHandle == Win32.GetForegroundWindow();
        Win32.SendMessage(windowHandle, activateNonClientArea, new IntPtr(isWindowActive ? 0 : 1), IntPtr.Zero);
        Win32.SendMessage(windowHandle, activateNonClientArea, new IntPtr(isWindowActive ? 1 : 0), IntPtr.Zero);

        // Needed to make the context menu theme change when you change the app theme after showing a window.
        Win32.FlushMenuThemes();
    }

    /// <inheritdoc />
    public virtual bool UserDefaultAppThemeIsDark {
        get {
            try {
                bool? oldValue = _userDefaultAppThemeIsDark;
                // Unfortunately, the corresponding undocumented uxtheme.dll function (#132) always returns Dark in .NET Core runtimes for some reason, so we check the registry instead.
                // Verified on Windows 10 21H2 and Windows 11 21H2.
                _userDefaultAppThemeIsDark = !Convert.ToBoolean(Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) ?? 1);
                if (oldValue is not null && _userDefaultAppThemeIsDark != oldValue) {
                    UserDefaultAppThemeIsDarkChanged?.Invoke(this, _userDefaultAppThemeIsDark.Value);
                }
            } catch (SecurityException) { } catch (IOException) { } catch (FormatException) { }

            return _userDefaultAppThemeIsDark ?? false;
        }
    }

    /// <inheritdoc />
    public virtual bool UserTaskbarThemeIsDark {
        get {
            try {
                bool? oldValue = _userTaskbarThemeIsDark;
                // In Windows 10 1809 and Server 2019, the taskbar is always dark, and this registry value does not exist.
                _userTaskbarThemeIsDark = !Convert.ToBoolean(Registry.GetValue(PersonalizeKey, "SystemUsesLightTheme", 0) ?? 0);
                if (oldValue is not null && _userTaskbarThemeIsDark != oldValue) {
                    UserTaskbarThemeIsDarkChanged?.Invoke(this, _userTaskbarThemeIsDark.Value);
                }
            } catch (SecurityException) { } catch (IOException) { } catch (FormatException) { }

            return _userTaskbarThemeIsDark ?? true;
        }
    }

    /// <inheritdoc />
    public virtual bool EffectiveCurrentProcessThemeIsDark {
        get => EffectiveProcessThemeIsDark ?? false;
        private set {
            if (value != EffectiveProcessThemeIsDark) {
                EffectiveProcessThemeIsDark = value;
                EffectiveCurrentProcessThemeIsDarkChanged?.Invoke(this, value);
            }
        }
    }

    private static bool IsWindowVisible(IntPtr windowHandle) {
        WindowInfo windowInfo = new(null);
        return Win32.GetWindowInfo(windowHandle, ref windowInfo) && (windowInfo.dwStyle & WindowStyles.WsVisible) != 0;
    }

    private static bool IsHighContrast() {
        const uint getHighContrast = 0x42;
        const uint highContrastOn  = 0x1;

        HighContrastData highContrastData = new(null);
        return Win32.SystemParametersInfo(getHighContrast, highContrastData.size, ref highContrastData, 0) && (highContrastData.flags & highContrastOn) != 0;
    }

    /// <inheritdoc cref="IDisposable.Dispose" />
    protected virtual void Dispose(bool disposing) {
        if (disposing) {
            try {
                SystemEvents.UserPreferenceChanged -= OnSettingsChanged;
            } catch (ExternalException) { }
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

}