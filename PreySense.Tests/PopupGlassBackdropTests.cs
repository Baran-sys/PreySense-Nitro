using System;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Effects;
using PreySense.Frontend.Services;

namespace PreySense.Tests;

[TestClass]
[DoNotParallelize]
public class PopupGlassBackdropTests
{
    [TestMethod]
    public void PreferredBackdrop_UsesAcrylicOnlyInGlassMode()
    {
        Assert.AreEqual(PopupGlassBackdrop.BackdropKind.Acrylic, PopupGlassBackdrop.PreferredBackdrop(true));
        Assert.AreEqual(PopupGlassBackdrop.BackdropKind.None, PopupGlassBackdrop.PreferredBackdrop(false));
    }

    [TestMethod]
    public void ExistingClosedPopup_UsesTheNewGlassSettingBeforeItsFirstOpening()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            bool previousGlass = ThemeManager.IsGlassEffectEnabled;
            bool previousLight = ThemeManager.IsLightTheme;
            var shadow = new DropShadowEffect();
            var host = new Border { Effect = shadow };
            var popup = new Popup { Child = host };
            try
            {
                ThemeManager.ApplyGlassEffect(false, persist: false);
                PopupGlassBackdrop.SetEnable(popup, true);
                Assert.IsTrue(popup.AllowsTransparency);
                Assert.AreSame(shadow, host.Effect);

                ThemeManager.ApplyGlassEffect(true, persist: false);
                PopupGlassBackdrop.RefreshTheme();
                Assert.IsFalse(popup.AllowsTransparency, "The first Glass opening must create a non-layered HWND.");
                Assert.IsNull(host.Effect);

                ThemeManager.ApplyGlassEffect(false, persist: false);
                PopupGlassBackdrop.RefreshTheme();
                Assert.IsTrue(popup.AllowsTransparency);
                Assert.AreSame(shadow, host.Effect);

                PopupGlassBackdrop.SetEnable(popup, false);
                ThemeManager.ApplyGlassEffect(true, persist: false);
                PopupGlassBackdrop.RefreshTheme();
                Assert.IsTrue(popup.AllowsTransparency, "Disabled attached behavior must stop changing the popup.");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                PopupGlassBackdrop.SetEnable(popup, false);
                ThemeManager.ApplyGlassEffect(previousGlass, persist: false);
                ThemeManager.Apply(previousLight, persist: false);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
