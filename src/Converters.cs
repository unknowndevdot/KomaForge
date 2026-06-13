using System;
using System.Globalization;
using System.Windows.Data;

namespace KomaForge;

// ListBox의 AlternationIndex(0부터)를 1부터의 번호로 표시하기 위한 변환기.
public sealed class AddOneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i ? i + 1 : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
