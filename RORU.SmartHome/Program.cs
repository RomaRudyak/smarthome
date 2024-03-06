using System;
using System.Device.Gpio;
using System.Device.I2c;
using System.Device.Spi;
using System.Diagnostics;
using System.Threading;

using Iot.Device;

using nanoFramework.Hardware.Esp32;

using RORU.SmartHome;

const int sleepTime = 60 * 1000;
//const int sleepTime = 1000;

Configuration.SetPinFunction(Gpio.IO21, DeviceFunction.I2C1_DATA);
Configuration.SetPinFunction(Gpio.IO22, DeviceFunction.I2C1_CLOCK);

Configuration.SetPinFunction(Gpio.IO18, DeviceFunction.SPI1_CLOCK);
Configuration.SetPinFunction(Gpio.IO23, DeviceFunction.SPI1_MOSI);

var dataCommandPin = Gpio.IO04;
var resetPin = Gpio.IO19;

SpiConnectionSettings spiConnectionSettings = new(1, 5)
{
    ClockFrequency = 5_000_000,
    Mode = SpiMode.Mode0,
    DataFlow = DataFlow.MsbFirst,
    ChipSelectLineActiveState = PinValue.Low
};
var spiDevice = new SpiDevice(spiConnectionSettings);

Pcd8544 lcd = new(dataCommandPin, spiDevice, resetPin, -1, null, false)
{
    Enabled = true
};

I2cDevice d = I2cDevice.Create(new(1, Sht21.I2cAddress));
using Sht21 sensor = new(d);

while (true)
{
    try
    {
        ClearOutput();

        var temp = sensor.Temperature.DegreesCelsius;
        var hum = sensor.Humidity.Percent;

        var tempStatus = IsTempGood(temp) ? "Good" : "Not good";
        WriteLine("Temperature:");
        WriteLine($" {temp:F0} C");
        WriteLine($" {tempStatus}");

        var humStatus = IsHumGood(hum) ? "Good" : "Not good";
        WriteLine("Humidity:");
        WriteLine($" {hum:F0} %");
        WriteLine($" {humStatus}");

        Thread.Sleep(sleepTime);
    }
    catch (Exception e)
    {
        Debug.WriteLine(e.Message);
    }
}

bool IsHumGood(double hum) => hum >= 50 && hum <= 70;

bool IsTempGood(double temp) => temp >= 18 && temp <= 22;

void WriteLine(string line = null)
{
    var v = line ?? string.Empty;
    lcd.WriteLine(v);
    Debug.WriteLine(v);
}

void ClearOutput()
{
    lcd.Clear();
}