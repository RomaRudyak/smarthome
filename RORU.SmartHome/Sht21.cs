using System.Device.I2c;
using System.Device.Model;
using System.Device;
using System;
using UnitsNet;

namespace RORU.SmartHome
{
    /// <summary>
    /// Temperature and Humidity Sensor Sht21
    /// </summary>
    [Interface("Temperature and Humidity Sensor Sht21")]
    public class Sht21
        : IDisposable
    {
        /// <summary>
        /// I2C device Address
        /// </summary>
        public const int I2cAddress = 0x40;

        public static Temperature TemperatureUndefined = default;
        public static RelativeHumidity HumidityUndefined = default;

        private const int CmdReadTempreture = 0xF3;
        private const int CmdReadHumidity = 0xF5;

        // wait about 1 ms
        private DateTime _lastMeasurement = DateTime.UtcNow;

        /// <summary>
        /// Read buffer
        /// </summary>
        protected byte[] _readBuff = new byte[3];

        /// <summary>
        /// I2C device used to communicate with the device
        /// </summary>
        protected I2cDevice _i2cDevice;

        /// <summary>
        /// Create a DHT sensor through I2C (Only DHT12)
        /// </summary>
        /// <param name="i2cDevice">The I2C device used for communication.</param>
        public Sht21(I2cDevice i2cDevice) => _i2cDevice = i2cDevice;

        /// <summary>
        /// How last read went, <c>true</c> for success, <c>false</c> for failure
        /// </summary>
        public bool IsLastReadSuccessful { get; internal set; }

        /// <summary>
        /// Get the last read temperature
        /// </summary>
        /// <remarks>
        /// If last read was not successful, it returns <code>default(Temperature)</code>
        /// </remarks>
        [Telemetry]
        public virtual Temperature Temperature
        {
            get
            {
                ReadData(CmdReadTempreture);
                return IsLastReadSuccessful
                    ? GetTemperature(_readBuff)
                    : TemperatureUndefined;
            }
        }

        /// <summary>
        /// Get the last read of relative humidity in percentage
        /// </summary>
        /// <remarks>
        /// If last read was not successful, it returns <code>default(RelativeHumidity)</code>
        /// </remarks>
        [Telemetry]
        public virtual RelativeHumidity Humidity
        {
            get
            {
                ReadData(CmdReadHumidity);
                return IsLastReadSuccessful
                    ? GetHumidity(_readBuff)
                    : HumidityUndefined;
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _i2cDevice?.Dispose();
            _i2cDevice = null;
        }

        /// <summary>
        /// Start a reading
        /// </summary>
        internal virtual void ReadData(byte command)
        {
            // The time of two measurements should be more than 1s.
            if (DateTime.UtcNow - _lastMeasurement < TimeSpan.FromSeconds(1))
            {
                return;
            }

            ReadThroughI2c(command);
        }

        /// <summary>
        /// Read through I2C
        /// </summary>
        internal virtual void ReadThroughI2c(byte command)
        {
            if (_i2cDevice is null)
            {
                throw new Exception("I2C device is not configured");
            }

            var res = _i2cDevice.WriteByte(command);
            if (res.Status != I2cTransferStatus.FullTransfer)
            {
                IsLastReadSuccessful = false;
                return;
            }

            DelayHelper.DelayMilliseconds(50, true);
            // humidity int, humidity decimal, temperature int, temperature decimal, checksum
            res = _i2cDevice.Read(_readBuff);

            _lastMeasurement = DateTime.UtcNow;

            IsLastReadSuccessful = res.Status == I2cTransferStatus.FullTransfer;
        }

        /// <summary>
        /// Converting data to humidity
        /// </summary>
        /// <param name="readBuff">Data</param>
        /// <returns>Humidity</returns>
        internal virtual RelativeHumidity GetHumidity(byte[] readBuff)
        {
            int rawValue = _readBuff[0] << 8;
            rawValue += _readBuff[1];
            rawValue &= 0xFFFC;

            var humidity = -6.0 + (125.0 / 65536.0) * rawValue;

            return RelativeHumidity.FromPercent(humidity);
        }

        /// <summary>
        /// Converting data to Temperature
        /// </summary>
        /// <param name="readBuff">Data</param>
        /// <returns>Temperature</returns>
        internal virtual Temperature GetTemperature(byte[] readBuff)
        {
            int rawValue = _readBuff[0] << 8;
            rawValue += _readBuff[1];
            rawValue &= 0xFFFC;

            var temp = -46.85 + (175.72 / 65536.0) * rawValue;

            return Temperature.FromDegreesCelsius(temp);
        }
    }
}
