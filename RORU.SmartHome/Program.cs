using nanoFramework.Hardware.Esp32;
using RORU.SmartHome;
using System;
using System.Device.I2c;
using System.Threading;

Configuration.SetPinFunction(21, DeviceFunction.I2C1_DATA);
Configuration.SetPinFunction(22, DeviceFunction.I2C1_CLOCK);

I2cDevice d = I2cDevice.Create(new(1, Sht21.I2cAddress));
using Sht21 sensor = new(d);

while (true)
{
    Console.WriteLine(new string('=', 10));
    Console.WriteLine($"Temperature:\t {sensor.Temperature.DegreesCelsius:F0}\u2103");
    Console.WriteLine($"Humidity:\t\t {sensor.Humidity.Percent:F0}%");
    Console.WriteLine(new string('=', 10));

    Thread.Sleep(2000);
}