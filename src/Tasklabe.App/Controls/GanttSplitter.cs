using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Tasklabe.App.Controls;

/// <summary>左右の領域の境界。ポインターを左右の矢印にする。</summary>
public sealed partial class GanttSplitter : Grid
{
    public GanttSplitter()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
