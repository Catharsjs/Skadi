using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace EventCapture.App.Services;

internal sealed class AudioDeviceNotificationClient(Action devicesChanged)
    : IMMNotificationClient
{
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        devicesChanged();

    public void OnDeviceAdded(string deviceId) => devicesChanged();

    public void OnDeviceRemoved(string deviceId) => devicesChanged();

    public void OnDefaultDeviceChanged(
        DataFlow flow,
        Role role,
        string defaultDeviceId) =>
        devicesChanged();

    public void OnPropertyValueChanged(string deviceId, PropertyKey key) =>
        devicesChanged();
}
