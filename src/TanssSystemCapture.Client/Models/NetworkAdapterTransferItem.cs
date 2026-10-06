using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TanssSystemCapture.Client.Models;

public sealed class NetworkAdapterTransferItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _sendAdapterName;
    private bool _sendMacAddress;
    private bool _dhcp;
    private string _transferRemark = string.Empty;
    private string _transferAdapterName = string.Empty;
    private string _transferMacAddress = string.Empty;
    private string _transferIpv4 = string.Empty;

    public NetworkAdapterTransferItem(NetworkAdapterInfo adapter, bool isSelectedByDefault)
    {
        Adapter = adapter;
        Reset(isSelectedByDefault);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public NetworkAdapterInfo Adapter { get; }

    public string Header => Adapter.DisplayName;

    public string TechnicalDetails =>
        $"Typ: {Adapter.InterfaceType} | DHCP: {Adapter.DhcpDisplay} | " +
        $"Standardgateway: {Adapter.DefaultGatewayDisplay}";

    public string WarningText => Adapter.IsLikelyVirtualOrSpecial
        ? "Hinweis: Dieser Adapter wirkt virtuell oder speziell. Bitte prüfen, ob er für das System relevant ist."
        : string.Empty;

    public string Ipv4Hint => Dhcp ? "Aktuelle IPv4-Adresse; bei DHCP wird sie nicht übertragen." : "Für DHCP die statische Adresse leeren und DHCP aktivieren.";
    public bool CanSelectIpv4 => !Dhcp;
    public bool CanUseDhcp => Dhcp || string.IsNullOrWhiteSpace(TransferIpv4);
    public bool Dhcp
    {
        get => _dhcp;
        set {
            if (value && !CanUseDhcp) return;
            if (SetField(ref _dhcp, value)) {
                if (value && string.IsNullOrWhiteSpace(_transferIpv4)) {
                    _transferIpv4 = Adapter.Ipv4Address;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TransferIpv4)));
                }
                NotifyMode();
            }
        }
    }
    public string TransferRemark { get => _transferRemark; set => SetField(ref _transferRemark, value); }
    public string? CombinedRemark => string.Join(" – ", new[] { SendAdapterName ? TransferAdapterName.Trim() : "", TransferRemark.Trim() }.Where(s => s.Length > 0)) is var text && text.Length > 0 ? text : null;
    private void NotifyMode() { foreach (var name in new[] { nameof(Dhcp), nameof(SendIpv4), nameof(CanSelectIpv4), nameof(CanUseDhcp), nameof(Ipv4Hint) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool SendAdapterName
    {
        get => _sendAdapterName;
        set => SetField(ref _sendAdapterName, value);
    }

    public bool SendMacAddress
    {
        get => _sendMacAddress;
        set => SetField(ref _sendMacAddress, value);
    }

    public bool SendIpv4 { get => !Dhcp && !string.IsNullOrWhiteSpace(TransferIpv4); set { _dhcp = !value; NotifyMode(); } }

    public string TransferAdapterName
    {
        get => _transferAdapterName;
        set => SetField(ref _transferAdapterName, value);
    }

    public string TransferMacAddress
    {
        get => _transferMacAddress;
        set => SetField(ref _transferMacAddress, value);
    }

    public string TransferIpv4
    {
        get => _transferIpv4;
        set { if (SetField(ref _transferIpv4, value)) NotifyMode(); }
    }

    public void Reset(bool isSelectedByDefault)
    {
        TransferAdapterName = Adapter.Name;
        TransferMacAddress = Adapter.MacAddress;
        TransferIpv4 = Adapter.Ipv4Address;

        SendAdapterName = true;
        SendMacAddress = true;
        _dhcp = Adapter.DhcpEnabled != false;
        TransferRemark = "";
        NotifyMode();
        IsSelected = isSelectedByDefault;
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}


