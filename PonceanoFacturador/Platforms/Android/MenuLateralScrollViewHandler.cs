using Android.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace PonceanoFacturador.Platforms.Android;

public sealed class MenuLateralScrollViewHandler : ScrollViewHandler
{
    protected override void DisconnectHandler(MauiScrollView platformView)
    {
        // MAUI 11 retains the inset panel's child when Shell recreates its flyout renderer.
        if (platformView.FindViewWithTag("MAUIContentInsetPanel") is ViewGroup panel)
            panel.RemoveAllViews();

        base.DisconnectHandler(platformView);
    }
}
