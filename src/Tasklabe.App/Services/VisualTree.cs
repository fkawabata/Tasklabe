using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Tasklabe.App.Services;

/// <summary>画面の要素の木（ビジュアルツリー）をたどる。</summary>
public static class VisualTree
{
    /// <summary>子孫を、親を子より先に、深さ優先で並べる。</summary>
    public static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }

    /// <inheritdoc cref="Descendants(DependencyObject)"/>
    public static IEnumerable<T> Descendants<T>(DependencyObject parent) => Descendants(parent).OfType<T>();

    /// <summary>要素自身から、根元へ向かって親をたどる。要素が null なら空。</summary>
    public static IEnumerable<DependencyObject> AncestorsAndSelf(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            yield return current;
        }
    }

    /// <summary>フォーカスのある要素から、根元へ向かって親をたどる。</summary>
    public static IEnumerable<DependencyObject> FocusedAncestors(XamlRoot? root) =>
        AncestorsAndSelf(root is null ? null : FocusManager.GetFocusedElement(root) as DependencyObject);

    /// <summary>要素が <paramref name="ancestor"/> そのものか、その中にある。</summary>
    public static bool IsWithin(DependencyObject? element, DependencyObject ancestor) =>
        AncestorsAndSelf(element).Any(e => ReferenceEquals(e, ancestor));
}
