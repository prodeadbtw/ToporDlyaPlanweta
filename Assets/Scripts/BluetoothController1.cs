using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using brab.bluetooth; // Используем твою библиотеку

public class BluetoothController : MonoBehaviour
{
    [Header("UI Элементы")]
    public TextMeshProUGUI statusText;
    public TextMeshProUGUI receivedDataText;
    public TMP_Dropdown deviceDropdown;
    public Button scanButton;
    public Button connectButton;
    public Button disconnectButton;
    public TMP_InputField messageInputField; // Поле для ввода сообщения для отправки

    private BluetoothAdapter btAdapter;
    private Dictionary<string, BluetoothDevice> discoveredDevices = new Dictionary<string, BluetoothDevice>();

    private BluetoothSocket socket;
    private BtStream iStream; // Поток для чтения
    private BtStream oStream; // Поток для записи
    private StreamReader reader;
    private StreamWriter writer;

    private bool isConnected = false;
    private StringBuilder receivedDataBuffer = new StringBuilder();

    // UUID для профиля SPP (Serial Port Profile), стандартный для HC-06
    private const string SPP_UUID = "00001101-0000-1000-8000-00805f9b34fb";

    void Start()
    {
        if (Application.platform != RuntimePlatform.Android)
        {
            SetStatus("Ошибка: Платформа не Android!", true);
            return;
        }

        btAdapter = BluetoothAdapter.getDefaultAdapter();
        if (btAdapter == null)
        {
            SetStatus("Ошибка: Bluetooth не поддерживается", true);
            return;
        }

        UpdateUIState();
    }

    #region --- Управление через UI ---

    public void OnScanButtonClick()
    {
        StartCoroutine(ScanDevices());
    }

    public void OnConnectButtonClick()
    {
        if (deviceDropdown.options.Count == 0)
        {
            SetStatus("Сначала найдите устройства");
            return;
        }

        string selectedDeviceName = deviceDropdown.options[deviceDropdown.value].text;
        if (discoveredDevices.TryGetValue(selectedDeviceName, out BluetoothDevice device))
        {
            StartCoroutine(ConnectToDevice(device));
        }
        else
        {
            SetStatus($"Ошибка: Устройство {selectedDeviceName} не найдено", true);
        }
    }

    public void OnDisconnectButtonClick()
    {
        Disconnect();
    }

    public void OnSendMessageButtonClick()
    {
        string message = messageInputField.text;
        if (!string.IsNullOrEmpty(message))
        {
            SendMessage(message);
        }
    }

    #endregion

    #region --- Основная логика Bluetooth ---

    private IEnumerator ScanDevices()
    {
        SetStatus("Поиск спаренных устройств...");
        deviceDropdown.ClearOptions();
        discoveredDevices.Clear();

        var bondedDevices = btAdapter.getBondedDevices();
        if (bondedDevices.Count > 0)
        {
            List<string> deviceNames = new List<string>();
            foreach (var device in bondedDevices)
            {
                // Используем имя и адрес для уникальности
                string deviceIdentifier = $"{device.getName()} [{device.getAddress()}]";
                discoveredDevices[deviceIdentifier] = device;
                deviceNames.Add(deviceIdentifier);
            }
            deviceDropdown.AddOptions(deviceNames);
            SetStatus("Выберите устройство и подключитесь");
        }
        else
        {
            SetStatus("Спаренные устройства не найдены");
        }
        yield return null;
    }

    private IEnumerator ConnectToDevice(BluetoothDevice device)
    {
        SetStatus($"Подключение к {device.getName()}...");
        scanButton.interactable = false;
        connectButton.interactable = false;

        // yield до try
        yield return new WaitForEndOfFrame();

        bool connectionSuccess = false;
        try
        {
            var uuid = UUID.fromString(SPP_UUID);
            socket = device.createRfcommSocketToServiceRecord(uuid);

            socket.connect();

            connectionSuccess = true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Ошибка подключения: {e.Message}");
            SetStatus($"Ошибка: {e.Message}", true);
            Disconnect();
        }

        // yield после try
        yield return new WaitForEndOfFrame();

        if (connectionSuccess)
        {
            iStream = socket.getInputStream();
            oStream = socket.getOutputStream();
            reader = new StreamReader(iStream, Encoding.ASCII);
            writer = new StreamWriter(oStream, Encoding.ASCII);

            isConnected = true;
            SetStatus($"Подключено к {device.getName()}");
        }
        UpdateUIState();
    }


    private void Disconnect()
    {
        if (!isConnected && socket == null) return;

        SetStatus("Отключено");
        isConnected = false;

        // Закрываем всё в обратном порядке и с проверками
        try { reader?.Close(); } catch { }
        try { writer?.Close(); } catch { }
        try { iStream?.Close(); } catch { }
        try { oStream?.Close(); } catch { }
        try { socket?.close(); } catch { }

        reader = null;
        writer = null;
        iStream = null;
        oStream = null;
        socket = null;

        UpdateUIState();
    }

    private void SendMessage(string message)
    {
        if (!isConnected || writer == null) return;

        try
        {
            // Добавляем символ новой строки, т.к. Arduino обычно читает до него
            writer.Write(message + "\n");
            writer.Flush(); // Обязательно сбрасываем буфер
        }
        catch (System.Exception e)
        {
            SetStatus($"Ошибка отправки: {e.Message}", true);
            Disconnect();
        }
    }

    #endregion

    #region --- Чтение данных и управление состоянием ---

    void Update()
    {
        // === НЕБЛОКИРУЮЩЕЕ ЧТЕНИЕ ===
        if (isConnected && reader != null && reader.Peek() > 0)
        {
            char[] buffer = new char[1024];
            int bytesRead = reader.Read(buffer, 0, buffer.Length);
            if (bytesRead > 0)
            {
                receivedDataBuffer.Append(buffer, 0, bytesRead);
                ProcessDataBuffer();
            }
        }
    }

    // Обрабатываем буфер, чтобы выделить полные сообщения (заканчивающиеся на '\n')
    private void ProcessDataBuffer()
    {
        string data = receivedDataBuffer.ToString();
        int newlineIndex;

        // Ищем разделитель (символ новой строки)
        while ((newlineIndex = data.IndexOf('\n')) != -1)
        {
            string message = data.Substring(0, newlineIndex).Trim();
            data = data.Substring(newlineIndex + 1);

            if (!string.IsNullOrEmpty(message))
            {
                // Мы получили полное сообщение!
                OnMessageReceived(message);
            }
        }
        // Обновляем буфер, оставляя в нем только "недочитанный" остаток
        receivedDataBuffer = new StringBuilder(data);
    }

    // Сюда приходят готовые сообщения от Arduino
    private void OnMessageReceived(string message)
    {
        Debug.Log($"Получено сообщение: {message}");
        receivedDataText.text = message; // Показываем последнее полное сообщение
        // !!! ЗДЕСЬ ТВОЯ ЛОГИКА ОБРАБОТКИ ДАННЫХ !!!
    }

    private void SetStatus(string message, bool isError = false)
    {
        statusText.text = $"Статус: {message}";
        if (isError) Debug.LogError(message); else Debug.Log(message);
    }

    private void UpdateUIState()
    {
        scanButton.interactable = !isConnected;
        connectButton.interactable = !isConnected;
        disconnectButton.interactable = isConnected;
    }

    void OnApplicationQuit()
    {
        Disconnect();
    }

    #endregion
}