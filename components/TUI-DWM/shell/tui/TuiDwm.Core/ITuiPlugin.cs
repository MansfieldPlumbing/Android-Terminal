using System;

namespace TuiDwm.Core;

public interface ITuiPlugin
{
    /// <summary>
    /// The VOMT declaring what VOM entries this plugin needs.
    /// </summary>
    Vomt GetTemplate();

    /// <summary>
    /// Called once when the plugin is loaded. The plugin receives
    /// a read-only reference to the VOM and its window's base path
    /// (e.g. "\\Windows\\0") so it can query its own state.
    /// </summary>
    void Initialize(Vom vom, string basePath);

    /// <summary>
    /// Called by the plugin's dedicated thread in a loop.
    /// The plugin should render its current state into the provided buffer.
    /// The buffer is the plugin's SINGLE texture — same instance every frame.
    /// </summary>
    void Render(CellBuffer buffer);

    /// <summary>
    /// Called when a key is pressed and this plugin has focus.
    /// </summary>
    void HandleInput(ConsoleKeyInfo key);

    /// <summary>
    /// Called when the compositor resizes this plugin's window.
    /// The plugin should adapt to the new dimensions.
    /// </summary>
    void OnResize(int newWidth, int newHeight);
}
