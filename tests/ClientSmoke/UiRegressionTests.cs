using System.Runtime.ExceptionServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class UiRegressionTests
{
    [Fact]
    public void LoginVmInvoicesOptionalFieldsAndDarkLayout()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Program.Run(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "WPF-Regression hat das Zeitlimit überschritten.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
