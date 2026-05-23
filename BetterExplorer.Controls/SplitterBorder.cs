using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace BetterExplorer.Controls;

/// <summary>
/// A <see cref="Grid"/> subclass that permanently shows the SizeWestEast
/// resize cursor, using the protected <see cref="Microsoft.UI.Xaml.UIElement.ProtectedCursor"/> API.
/// </summary>
internal sealed class SplitterBorder : Grid
{
    public SplitterBorder()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
