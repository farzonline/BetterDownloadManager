using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using BDM.App.Views;

namespace BDM.App.Tests;

/// <summary>Clicks must be felt: buttons sink while held, and the sorted column says which way it is sorted.</summary>
public class FeedbackTests
{
    static void OnSta(Action a)
    {
        Exception? error = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
    }

    [Fact]
    public void AButton_SinksWhenPressed_AndSpringsBackWhenReleased() => OnSta(() =>
    {
        var b = new Button();
        PressFeedback.Press(b);
        var scale = Assert.IsType<ScaleTransform>(b.RenderTransform);
        Assert.True(scale.HasAnimatedProperties, "pressing should animate the button down");
        PressFeedback.Release(b);
        Assert.True(scale.HasAnimatedProperties);
    });

    [Fact]
    public void ADisabledButton_DoesNotMove() => OnSta(() =>
    {
        var b = new Button { IsEnabled = false };
        PressFeedback.Press(b);
        Assert.IsNotType<ScaleTransform>(b.RenderTransform);
    });

    [Theory]
    [InlineData("Size", ListSortDirection.Ascending, "Size  ▲")]
    [InlineData("Size  ▲", ListSortDirection.Descending, "Size  ▼")]
    [InlineData("Size  ▼", null, "Size")]
    public void TheSortedColumn_ShowsAnArrow_AndTheOthersLoseTheirs(string header, ListSortDirection? dir, string expected) =>
        Assert.Equal(expected, MainWindow.SortLabel(header, dir));
}
