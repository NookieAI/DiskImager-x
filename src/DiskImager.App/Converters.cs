using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DiskImagerX;

/// <summary>Brushes/values that light up the active mode tab (Mode == parameter).</summary>
public sealed class TabBrush : IValueConverter
{
    public static readonly TabBrush Bg = new() { Active = new SolidColorBrush(Color.Parse("#16324A")), Inactive = Brushes.Transparent };
    public static readonly TabBrush Fg = new() { Active = new SolidColorBrush(Color.Parse("#DCEBFF")), Inactive = new SolidColorBrush(Color.Parse("#6280A0")) };
    public static readonly TabBrush Accent = new() { Active = new SolidColorBrush(Color.Parse("#00D2FF")), Inactive = Brushes.Transparent };

    public IBrush Active { get; init; } = Brushes.Transparent;
    public IBrush Inactive { get; init; } = Brushes.Transparent;

    public object Convert(object? value, Type t, object? param, CultureInfo c)
    {
        bool on = value is int m && param is string s && int.TryParse(s, out var i) && m == i;
        return on ? Active : Inactive;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Inverts a bool (for enabling controls while not running, etc.).</summary>
public sealed class Not : IValueConverter
{
    public static readonly Not Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is bool b && !b;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => v is bool b && !b;
}

/// <summary>bool → one of two brushes. Used for the elevation status (ok green / warn amber).</summary>
public sealed class BoolBrush : IValueConverter
{
    public static readonly BoolBrush Status = new() { True = new SolidColorBrush(Color.Parse("#1FD17A")), False = new SolidColorBrush(Color.Parse("#FF9838")) };
    public static readonly BoolBrush StatusFaint = new() { True = new SolidColorBrush(Color.Parse("#132A20")), False = new SolidColorBrush(Color.Parse("#2A2010")) };
    public IBrush True { get; init; } = Brushes.Transparent;
    public IBrush False { get; init; } = Brushes.Transparent;
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? True : False;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
