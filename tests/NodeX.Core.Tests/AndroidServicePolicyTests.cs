namespace NodeX.Core.Tests;

public class AndroidServicePolicyTests
{
    [Theory]
    [InlineData(33, BackgroundServiceKinds.ConnectedDevice)]
    [InlineData(34, BackgroundServiceKinds.SpecialUse)]
    [InlineData(35, BackgroundServiceKinds.SpecialUse)]
    public void IdleRegistration_DoesNotCaptureMicOrHoldLocks(int api, BackgroundServiceKinds expected)
    {
        var plan = AndroidServicePlan.Create(new(true, null, true, ""), api, true, true, false, true);
        Assert.Equal(expected, plan.Kinds);
        Assert.False(plan.HoldLocks);
        Assert.False(plan.CarAudio);
        Assert.False(plan.BluetoothSco);
        Assert.False(plan.HeadsetKeys);
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    public void LinkedRoutes_RespectCarAndSpeakerChoice(bool car, bool speaker, bool sco, bool keys)
    {
        var plan = AndroidServicePlan.Create(new(true, "2000", true, ""), 35, true, car, speaker, true);
        Assert.True(plan.HoldLocks);
        Assert.Equal(car, plan.CarAudio);
        Assert.Equal(sco, plan.BluetoothSco);
        Assert.Equal(keys, plan.HeadsetKeys);
        Assert.True(plan.Kinds.HasFlag(BackgroundServiceKinds.Microphone));
    }

    [Fact]
    public void MicDenied_StillPlaysAndHoldsLink_WithoutMicType()
    {
        var plan = AndroidServicePlan.Create(new(true, "2000", true, ""), 35, false, true, false, false);
        Assert.Equal(BackgroundServiceKinds.MediaPlayback | BackgroundServiceKinds.ConnectedDevice, plan.Kinds);
        Assert.True(plan.HoldLocks);
    }

    [Fact]
    public void Exit_ReleasesEveryResource()
    {
        Assert.Equal(new AndroidServicePlan(BackgroundServiceKinds.None, false, false, false, false),
            AndroidServicePlan.Create(new(false, null, false, ""), 35, true, true, true, true));
    }

    [Fact]
    public void NotificationRefresh_DoesNotRestartMicrophoneService_ButTypeChangeDoes()
    {
        Assert.False(AndroidServicePlan.NeedsForegroundStart(true, 7, 7));
        Assert.True(AndroidServicePlan.NeedsForegroundStart(true, 3, 7));
        Assert.True(AndroidServicePlan.NeedsForegroundStart(false, 7, 7));
        Assert.True(AndroidServicePlan.NeedsForegroundStart(true, null, 7));
    }

    [Theory]
    [InlineData(CarMediaButton.Play)]
    [InlineData(CarMediaButton.Pause)]
    [InlineData(CarMediaButton.Stop)]
    [InlineData(CarMediaButton.Next)]
    [InlineData(CarMediaButton.Previous)]
    public void EveryMediaButton_OnlyUnkeysWhileTransmitting(CarMediaButton button) =>
        Assert.Equal(CarMediaAction.Unkey, CarMediaButtons.Decide(button, true, true, true));

    [Theory]
    [InlineData(CarMediaButton.Play, false, false, CarMediaAction.Ignore)]
    [InlineData(CarMediaButton.Stop, false, false, CarMediaAction.Disconnect)]
    [InlineData(CarMediaButton.Pause, false, false, CarMediaAction.Disconnect)]
    [InlineData(CarMediaButton.Next, false, false, CarMediaAction.Next)]
    [InlineData(CarMediaButton.Previous, false, false, CarMediaAction.Previous)]
    [InlineData(CarMediaButton.Next, true, false, CarMediaAction.Talk)]
    [InlineData(CarMediaButton.Previous, false, true, CarMediaAction.Talk)]
    public void IdleMediaButtons_RequireAnExplicitTalkMapping(CarMediaButton button, bool next, bool previous, CarMediaAction expected) =>
        Assert.Equal(expected, CarMediaButtons.Decide(button, false, next, previous));
}
