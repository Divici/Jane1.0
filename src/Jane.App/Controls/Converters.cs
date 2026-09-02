using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Jane.App.Controls;

/// <summary>Collapses an element when its bound value is null, empty or whitespace.</summary>
/// <remarks>
/// Used for every optional line in these windows -- a description, a warning, a licence note.
/// Collapsing rather than hiding matters: a reserved empty line under every setting turns a
/// dense list into a sparse one for no information at all.
/// </remarks>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            null => Visibility.Collapsed,
            string text => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible,
            _ => Visibility.Visible,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True becomes <see cref="Visibility.Visible"/>; pass <c>Invert</c> to reverse it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "Invert", StringComparison.Ordinal))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>Maps <see cref="Severity"/> onto the palette, so colour is defined once.</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    /// <summary>Set to true for the background wash rather than the foreground colour.</summary>
    public bool Wash { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var severity = value as Severity? ?? Severity.Info;
        var wash = Wash || string.Equals(parameter as string, "Wash", StringComparison.Ordinal);

        return severity switch
        {
            Severity.Success => wash ? JaneTheme.SuccessWash : JaneTheme.Success,
            Severity.Warning => wash ? JaneTheme.CautionWash : JaneTheme.Caution,
            Severity.Error => wash ? JaneTheme.DangerWash : JaneTheme.Danger,
            _ => wash ? (Brush)JaneTheme.AccentWashStrong : JaneTheme.TextSecondary,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible only when the bound collection is empty. Drives every designed empty state.</summary>
public sealed class EmptyCollectionToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            null => 0,
            int number => number,
            System.Collections.ICollection collection => collection.Count,
            System.Collections.IEnumerable items => items.Cast<object>().Count(),
            _ => 1,
        };

        var empty = count == 0;
        if (string.Equals(parameter as string, "Invert", StringComparison.Ordinal))
        {
            empty = !empty;
        }

        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// True when the first bound step has been reached by the second.
/// </summary>
/// <remarks>
/// Drives the wizard's progress rail: a step is lit when it is the current one or an earlier one.
/// Written against <see cref="Enum"/> rather than a specific type so the rail does not have to
/// know what it is stepping through.
/// </remarks>
public sealed class StepReachedConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [Enum step, Enum current] &&
        System.Convert.ToInt32(step, culture) <= System.Convert.ToInt32(current, culture);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
