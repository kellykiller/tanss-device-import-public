using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TanssSystemCapture.Client.Models;

public sealed class NetworkAdapterTransferItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _sendAdapterName;
    private bool _sendMacAddress;
    private bool _sendIpv4;
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

    public string Ipv4Hint => Adapter.DhcpEnabled switch
    {
        true => "DHCP ist aktiv. Die aktuell geleaste IPv4-Adresse wird nicht als statische TANSS-IP angeboten.",
        false => "DHCP ist nicht aktiv. Die IPv4-Adresse kann angepasst oder abgewählt werden.",
        null => "Der DHCP-Status ist nicht sicher ermittelbar. Die IPv4-Adresse bleibt vorsorglich abgewählt."
    };

    public bool CanSelectIpv4 => Adapter.DhcpEnabled != true;

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

    public bool SendIpv4
    {
        get => _sendIpv4;
        set
        {
            var permittedValue = CanSelectIpv4 && value;
            SetField(ref _sendIpv4, permittedValue);
        }
    }

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
        set => SetField(ref _transferIpv4, value);
    }

    public void Reset(bool isSelectedByDefault)
    {
        TransferAdapterName = Adapter.Name;
        TransferMacAddress = Adapter.MacAddress;
        TransferIpv4 = Adapter.Ipv4Address;

        SendAdapterName = true;
        SendMacAddress = true;
        SendIpv4 = Adapter.DhcpEnabled == false;
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

