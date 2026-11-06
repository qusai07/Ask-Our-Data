using Microsoft.JSInterop;

namespace AskOurData.Web.Components;

/// <summary>
/// UI-only JS calls (focus, scroll, copy, download). If the browser has gone away (tab closed, reload,
/// sign-out or language switch while an answer is running) the circuit is disposed and these calls
/// are simply skipped instead of throwing JSDisconnectedException.
/// </summary>
public static class SafeJs
{
    public static async Task TryInvokeVoidAsync(this IJSRuntime js, string identifier, params object?[] args)
    {
        try
        {
            await js.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}
