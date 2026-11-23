#if ANDROID
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using MauiButton = Microsoft.Maui.Controls.Button;
using MauiScrollView = Microsoft.Maui.Controls.ScrollView;
using NativeColor = Android.Graphics.Color;

namespace PonceanoFacturador;

internal static class AndroidMobileUi
{
    public static void EnableHorizontalTableScroll(Border table, double width)
    {
        if (table.Parent is not Grid parent)
            throw new InvalidOperationException("La tabla debe pertenecer a un Grid para habilitar el desplazamiento horizontal.");

        var row = Grid.GetRow(table);
        var column = Grid.GetColumn(table);
        var rowSpan = Grid.GetRowSpan(table);
        var columnSpan = Grid.GetColumnSpan(table);
        parent.Remove(table);
        table.WidthRequest = width;

        var scroll = new MauiScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Always,
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = table
        };
        Grid.SetRow(scroll, row);
        Grid.SetColumn(scroll, column);
        Grid.SetRowSpan(scroll, rowSpan);
        Grid.SetColumnSpan(scroll, columnSpan);
        parent.Add(scroll);
    }

    public static Task<string?> ShowRowActionsAsync(MauiButton anchor, string title, IReadOnlyList<string> options)
    {
        if (anchor.Handler?.PlatformView is not Android.Views.View nativeAnchor)
            throw new InvalidOperationException("No se pudo encontrar el botón de acciones en Android.");

        var context = nativeAnchor.Context ?? throw new InvalidOperationException("No se pudo encontrar el contexto Android del botón.");
        var density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        var layout = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var background = new GradientDrawable();
        background.SetColor(NativeColor.White);
        background.SetCornerRadius(8 * density);
        layout.Background = background;
        layout.SetPadding((int)(4 * density), (int)(4 * density), (int)(4 * density), (int)(4 * density));

        var titleView = new TextView(context)
        {
            Text = title,
            TextSize = 14,
            Typeface = Typeface.Create("sans-serif-medium", TypefaceStyle.Normal),
            Gravity = GravityFlags.CenterVertical,
            Ellipsize = Android.Text.TextUtils.TruncateAt.End
        };
        titleView.SetMaxLines(1);
        layout.AddView(titleView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(48 * density)));

        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var popup = new PopupWindow(layout, (int)(260 * density), ViewGroup.LayoutParams.WrapContent, true)
        {
            OutsideTouchable = true,
            Elevation = 8 * density
        };
        popup.SetBackgroundDrawable(new ColorDrawable(NativeColor.White));
        popup.DismissEvent += (_, _) => result.TrySetResult(null);

        foreach (var option in options)
        {
            var item = new TextView(context)
            {
                Text = option,
                TextSize = 14,
                Gravity = GravityFlags.CenterVertical,
                Ellipsize = Android.Text.TextUtils.TruncateAt.End,
                Clickable = true,
                Focusable = true
            };
            item.SetMaxLines(2);
            item.SetTextColor(NativeColor.Argb(255, 38, 55, 70));
            item.SetPadding((int)(12 * density), (int)(8 * density), (int)(12 * density), (int)(8 * density));
            item.SetBackgroundResource(global::Android.Resource.Drawable.ListSelectorBackground);
            item.Click += (_, _) =>
            {
                result.TrySetResult(option);
                popup.Dismiss();
            };
            layout.AddView(item, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(48 * density)));
        }

        popup.ShowAsDropDown(nativeAnchor, 0, 0, GravityFlags.End);
        return result.Task;
    }
}
#endif
