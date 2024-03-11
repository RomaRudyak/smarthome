using System;
using System.Collections;
using System.Device.Gpio;
using System.Device.Pwm;
using System.Device.Spi;
using System.Threading;

using Iot.Device.CharacterLcd;
using Iot.Device.Graphics;

using nanoFramework.UI;

namespace RORU.SmartHome
{
    public class Pcd8544 : IDisposable
    {
        private const int CharacterWidth = 6;

        /// <summary>
        /// The size of the screen in terms of pixels.
        /// </summary>
        public static Size PixelScreenSize => new Size(84, 48);

        /// <summary>
        /// Size of the screen 48 x 84 / 8 in bytes.
        /// </summary>
        public const int ScreenBufferByteSize = 504;

        /// <summary>
        /// The size of the screen in terms of characters.
        /// </summary>
        public Size Size => new Size(14, 6);

        /// <summary>
        /// Number of bit per pixel for the color.
        /// </summary>
        public const int ColorBitPerPixel = 1;

        private readonly byte[] _byteMap = new byte[504];
        private readonly int _dataCommandPin;
        private readonly int _resetPin;
        private readonly int _backlightPin = -1;
        private PwmChannel? _pwmBacklight;
        private SpiDevice? _spiDevice;
        private GpioController? _controller;
        private bool _shouldDispose;
        private float _backlightVal = 0;
        private bool _invd = false;
        private byte _contrast = 0;
        private int _position;
        private bool _cursorVisible = false;
        private byte _bias;
        private bool _enabled;
        private ScreenTemperature _temperature;
        private BdfFont _font;

        /// <summary>
        /// Initializes a new instance of the <see cref="Pcd8544" /> class.
        /// </summary>
        /// <param name="dataCommandPin">The data command pin.</param>
        /// <param name="spiDevice">The SPI device.</param>
        /// <param name="resetPin">The reset pin. Use a negative number if you don't want to use it.</param>
        /// <param name="pwmBacklight">The PWM channel for the back light.</param>
        /// <param name="gpioController">The GPIO Controller.</param>
        /// <param name="shouldDispose">True to dispose the GPIO controller.</param>
        /// <exception cref="ArgumentOutOfRangeException">Invalid 'Data Command' pin number. Pin number can not be less than zero.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="spiDevice"/> is null.</exception>
        public Pcd8544(
            int dataCommandPin,
            SpiDevice spiDevice,
            int resetPin = -1,
            PwmChannel? pwmBacklight = null,
            GpioController? gpioController = null,
            bool shouldDispose = true)
        {
            if (dataCommandPin < 0)
            {
                throw new ArgumentOutOfRangeException();
            }

            _dataCommandPin = dataCommandPin;
            _pwmBacklight = pwmBacklight;
            _pwmBacklight?.Start();
            _spiDevice = spiDevice ?? throw new ArgumentNullException(nameof(spiDevice));
            _shouldDispose = gpioController == null || shouldDispose;
            _controller = gpioController ?? new();
            _resetPin = resetPin;
            if (resetPin >= 0)
            {
                _controller.OpenPin(resetPin, PinMode.Output);
                _controller.Write(resetPin, PinValue.Low);

                // Doc says at least 100 ns
                Thread.Sleep(1);
                _controller.Write(resetPin, PinValue.High);
            }

            _controller.OpenPin(_dataCommandPin, PinMode.Output);

            Initialize();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Pcd8544" /> class.
        /// </summary>
        /// <param name="dataCommandPin">The data command pin.</param>
        /// <param name="spiDevice">The SPI device.</param>
        /// <param name="resetPin">The reset pin. Use a negative number if you don't want to use it.</param>
        /// <param name="backlightPin">The pin back light.</param>
        /// <param name="gpioController">The GPIO Controller.</param>
        /// <param name="shouldDispose">True to dispose the GPIO controller.</param>
        /// <exception cref="ArgumentOutOfRangeException">Invalid 'Data Command' pin number. Pin number can not be less than zero.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="spiDevice"/> is null.</exception>
        public Pcd8544(
            int dataCommandPin,
            SpiDevice spiDevice,
            int resetPin = -1,
            int backlightPin = -1,
            GpioController? gpioController = null,
            bool shouldDispose = true)
        {
            if (dataCommandPin < 0)
            {
                throw new ArgumentOutOfRangeException();
            }

            _dataCommandPin = dataCommandPin;
            _spiDevice = spiDevice ?? throw new ArgumentNullException(nameof(spiDevice));
            _shouldDispose = gpioController == null || shouldDispose;
            _controller = gpioController ?? new();
            _resetPin = resetPin;
            if (resetPin >= 0)
            {
                _controller.OpenPin(resetPin, PinMode.Output);
                _controller.Write(resetPin, PinValue.Low);

                // Doc says at least 100 ns
                Thread.Sleep(1);
                _controller.Write(resetPin, PinValue.High);
            }

            _backlightPin = backlightPin;
            if (backlightPin >= 0)
            {
                _controller.OpenPin(backlightPin, PinMode.Output);
                _controller.Write(backlightPin, PinValue.Low);
            }

            _controller.OpenPin(_dataCommandPin, PinMode.Output);

            Initialize();
        }

        private void Initialize()
        {
            _font = new Font5x8();
            _bias = 4;
            _temperature = ScreenTemperature.Coefficient0;
            _contrast = 0x30;
            _enabled = true;

            // Extended function, contrast to 0x30, temperature to coef 0, bias to 4, Screen to normal display power on, display to normal mode
            SpanByte toSend = new byte[] { (byte)(FunctionSet.PowerOn | FunctionSet.ExtendedMode), (byte)(0x80 | _contrast), (byte)ScreenTemperature.Coefficient0, (byte)(0x10 | _bias), (byte)FunctionSet.PowerOn, (byte)PcdDisplayControl.NormalMode };
            SpiWrite(false, toSend);
            Clear();
            Draw();
        }

        #region properties

        /// <summary>
        /// Gets or sets a value indicating whether the screen is enabled.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                byte enab = (byte)(_enabled ? FunctionSet.PowerOn : FunctionSet.PowerOff);
                SpanByte toSend = new byte[] { enab };
                SpiWrite(false, toSend);
            }
        }

        /// <summary>
        /// Gets or sets the brightness level of the LCD backlight.
        /// Supported values are ftrom 0.0 to 1.0.
        /// If a pin is used, the threshold for full light is more then 0.5.
        /// </summary>
        public float BacklightBrightness
        {
            get => _backlightVal;
            set
            {
                _backlightVal = value > 1 ? 1 : value;
                _backlightVal = _backlightVal < 0 ? 0 : _backlightVal;
                if (_pwmBacklight != null)
                {
                    _pwmBacklight.DutyCycle = _backlightVal;
                }

                if (_backlightPin >= 0)
                {
                    _controller.Write(_backlightPin, _backlightVal > 0.5 ? PinValue.High : PinValue.Low);
                }
            }
        }

        /// <summary>
        /// Gets or sets the bias. Supported values are from 0 to 7. Bias represent the voltage applied to the LCD. The highest, the darker the screen will be.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Bias value can not be more than 7.</exception>
        public byte Bias
        {
            get => _bias;
            set
            {
                _bias = value < 8 ? value : throw new ArgumentOutOfRangeException();
                SpanByte toSend = new byte[] { (byte)(_enabled ? FunctionSet.PowerOn | FunctionSet.ExtendedMode : FunctionSet.PowerOff | FunctionSet.ExtendedMode), (byte)(0x10 | _bias) };
                SpiWrite(false, toSend);
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the screen colors are inverted.
        /// </summary>
        public bool InvertedColors
        {
            get => _invd;
            set
            {
                _invd = value;
                SpanByte toSend = _invd ? new byte[] { (byte)(_enabled ? FunctionSet.PowerOn : FunctionSet.PowerOff), (byte)PcdDisplayControl.InverseVideoMode } : new byte[] { (byte)FunctionSet.PowerOn, (byte)PcdDisplayControl.NormalMode };
                SpiWrite(false, toSend);
            }
        }

        /// <summary>
        /// Gets or sets the contrast. Accepted values are from 0 to 127.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Contrast value must be between 0 and 127.</exception>
        public byte Contrast
        {
            get => _contrast;
            set
            {
                _contrast = value >= 0 && value <= 127 ? value : throw new ArgumentOutOfRangeException();
                SpanByte toSend = new byte[] { (byte)(_enabled ? FunctionSet.PowerOn | FunctionSet.ExtendedMode : FunctionSet.PowerOff | FunctionSet.ExtendedMode), (byte)(0x80 | _contrast) };
                SpiWrite(false, toSend);
            }
        }

        /// <summary>
        /// Gets or sets the temperature coefficient.
        /// </summary>
        public ScreenTemperature Temperature
        {
            get => _temperature;
            set
            {
                SpanByte toSend = new byte[] { (byte)(_enabled ? FunctionSet.PowerOn | FunctionSet.ExtendedMode : FunctionSet.PowerOff | FunctionSet.ExtendedMode), (byte)value };
                SpiWrite(false, toSend);
            }
        }

        /// <inheritdoc/>
        public bool BacklightOn { get => BacklightBrightness > 0; set => BacklightBrightness = value ? 1.0f : 0.0f; }

        /// <inheritdoc/>
        public bool DisplayOn { get => Enabled; set => Enabled = value; }

        /// <inheritdoc/>
        public bool UnderlineCursorVisible
        {
            get => _cursorVisible;
            set
            {
                // If the cursor was visible, we remove it
                if (_cursorVisible && !value)
                {
                    DrawCursor();
                }

                _cursorVisible = value;
                if (_cursorVisible)
                {
                    DrawCursor();
                }
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the blinking cursor is visible. This is not supported on this screen, this function will have no effect.
        /// </summary>
        public bool BlinkingCursorVisible { get; set; }

        /// <inheritdoc/>
        public int NumberOfCustomCharactersSupported => char.MaxValue;

        #endregion

        #region Primitive methods

        /// <summary>
        /// Draw what's in memory to the the screen.
        /// </summary>
        public void Draw() => SpiWrite(true, _byteMap);

        /// <summary>
        /// Clear the screen.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _byteMap.Length; i++)
            {
                _byteMap[i] = 0;
            }

            SetCursorPosition(0, 0);
            Draw();
        }

        /// <summary>
        /// Set the byte map.
        /// </summary>
        /// <param name="byteMap">A 504 sized byte representing the full image.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteMap"/> length must be equal to <see cref="ScreenBufferByteSize"/></exception>
        public void SetByteMap(SpanByte byteMap)
        {
            if (byteMap.Length != ScreenBufferByteSize)
            {
                throw new ArgumentOutOfRangeException();
            }

            SetCursorPosition(0, 0);
            byteMap.CopyTo(_byteMap);
        }

        private byte[] GetCharBytes(char @char)
        {
            _font.GetCharData(@char, out SpanUshort charData);
            byte[] characterMap = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                characterMap[i] = (byte)charData[i];
            }

            if (@char >= NumberOfCustomCharactersSupported)
            {
                throw new ArgumentOutOfRangeException();
            }

            byte[] character = LcdCharacterEncodingFactory.ConvertFont8to5bytes(characterMap);

            return character;
        }

        #endregion

        #region Text

        /// <summary>
        /// Write text.
        /// </summary>
        /// <param name="text">The text to write.</param>
        public void Write(string text)
        {
            foreach (char c in text)
            {
                // We only display specific characters and ignore the rest
                // And only if it's in the screen
                if (_position <= ScreenBufferByteSize - CharacterWidth)
                {
                    WriteChar(c);
                }
            }

            if (_cursorVisible)
            {
                DrawCursor();
            }
        }

        /// <summary>
        /// Write a raw byte stream to the display.
        /// Used if character translation already took place.
        /// </summary>
        /// <param name="text">Text to print.</param>
        public void Write(SpanChar text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                WriteChar(text[i]);
            }

            if (_cursorVisible)
            {
                DrawCursor();
            }
        }

        /// <summary>
        /// Write a raw byte stream to the display.
        /// Used if character translation already took place.
        /// </summary>
        /// <param name="text">Text to print.</param>
        public void Write(char[] text)
        {
            foreach (var c in text)
            {
                WriteChar(c);
            }

            if (_cursorVisible)
            {
                DrawCursor();
            }
        }

        private void WriteChar(char c)
        {

            SpanByte letter = new byte[CharacterWidth];
            bool isChar = Array.IndexOf(_font.SupportedChars, c) != -1;
            if (isChar)
            {
                if (isChar)
                {
                    
                    var font = GetCharBytes(c);
                    for (int i = 0; i < CharacterWidth - 1; i++)
                    {
                        letter[i] = font[i];
                    }
                }

                letter[5] = 0x00;

                if (_position < _byteMap.Length)
                {
                    letter.CopyTo(new SpanByte(_byteMap, _position, _byteMap.Length - _position));
                }

                SpiWrite(true, letter);
                _position += CharacterWidth;
            }
            else if (c == 0x08)
            {
                // Case of backspace, we go back
                if (_cursorVisible)
                {
                    DrawCursor();
                }

                _position -= CharacterWidth;
                _position = _position < 0 ? 0 : _position;
                SetPosition(_position);
                SpiWrite(true, letter);

                if (_position < _byteMap.Length)
                {
                    letter.CopyTo(new SpanByte(_byteMap, _position, _byteMap.Length - _position));
                }

                SetPosition(_position);
            }
        }

        /// <summary>
        /// Write text and set cursor position to next line.
        /// </summary>
        /// <param name="text">The text to write.</param>
        public void WriteLine(string text)
        {
            Write(text);

            // calculate the position
            int y = (_position / (Size.Width * CharacterWidth)) + 1;
            y = y > Size.Height ? Size.Height : y;

            SetCursorPosition(0, y);
        }

        private void DrawCursor()
        {
            // We won't draw the cursor outside of the screen
            if (_position > ScreenBufferByteSize - CharacterWidth)
            {
                return;
            }

            SpanByte letter = new byte[CharacterWidth];
            for (int i = 0; i < CharacterWidth - 1; i++)
            {
                _byteMap[_position + i] = (byte)((_byteMap[_position + i] & 0x80) == 0x80 ? _byteMap[_position + i] & 0x7F : _byteMap[_position + i] | 0x80);
                letter[i] = _byteMap[_position + i];
            }

            SpiWrite(true, letter);
            SetPosition(_position);
        }

        /// <summary>
        /// Moves the cursor to an explicit column and row position.
        /// </summary>
        /// <param name="left">The column position from left to right starting with 0 to 14.</param>
        /// <param name="top">The row position from the top starting with 0 to 5.</param>
        /// <exception cref="ArgumentOutOfRangeException">The given position is not inside the display.</exception>
        public void SetCursorPosition(int left, int top)
        {
            if ((left < 0 || left > Size.Width) || (top < 0 || top > Size.Height))
            {
                throw new ArgumentOutOfRangeException();
            }

            if (_cursorVisible)
            {
                DrawCursor();
            }

            SetPosition(left * CharacterWidth, top);
            _position = ((left * CharacterWidth) + top) * Size.Width * CharacterWidth;
            if (_cursorVisible)
            {
                DrawCursor();
            }
        }

        private void SetPosition(int left, int top)
        {
            SpanByte toSend = new byte[] { (byte)(_enabled ? FunctionSet.PowerOn : FunctionSet.PowerOff), (byte)((byte)SetAddress.XAddress | left), (byte)((byte)SetAddress.YAddress | top) };
            SpiWrite(false, toSend);
        }

        private void SetPosition(int position)
        {
            int top = position / (Size.Width * CharacterWidth);
            int left = (position - top) * Size.Width * CharacterWidth;
            SetPosition(left, top);
        }

        #endregion

        #region Drawing points, lines and rectangles

        /// <summary>
        /// Draw a point.
        /// </summary>
        /// <param name="x">The X coordinate.</param>
        /// <param name="y">The Y coordinate.</param>
        /// <param name="isOn">True if the point has pixels on, false for off.</param>
        /// <returns>True if success.</returns>
        public bool DrawPoint(int x, int y, bool isOn)
        {
            if (x < 0 || x >= 84 || y < 0 || y >= 48)
            {
                return false;
            }

            int index = (x % 84) + (y / 8 * 84);

            byte bitMask = (byte)(1 << (y % 8));

            if (isOn)
            {
                _byteMap[index] |= bitMask;
            }
            else
            {
                _byteMap[index] &= (byte)~bitMask;
            }

            return true;
        }

        /// <summary>
        /// Draw a point.
        /// </summary>
        /// <param name="point">The point to draw.</param>
        /// <param name="isOn">True if the point has pixels on, false for off.</param>
        /// <returns>True if success.</returns>
        public bool DrawPoint(Point point, bool isOn) => DrawPoint(point.X, point.Y, isOn);

        /// <summary>
        /// Draw a line.
        /// </summary>
        /// <param name="x1">The first point X coordinate.</param>
        /// <param name="y1">The first point Y coordinate.</param>
        /// <param name="x2">The second point X coordinate.</param>
        /// <param name="y2">The second point Y coordinate.</param>
        /// <param name="isOn">True if the line has pixels on, false for off.</param>
        public void DrawLine(int x1, int y1, int x2, int y2, bool isOn)
        {
            // This is a common line drawing algorithm. Read about it here:
            // http://en.wikipedia.org/wiki/Bresenham's_line_algorithm
            int sx = (x1 < x2) ? 1 : -1;
            int sy = (y1 < y2) ? 1 : -1;

            int dx = x2 > x1 ? x2 - x1 : x1 - x2;
            int dy = y2 > x1 ? y2 - y1 : y1 - y2;

            float err = dx - dy, e2;

            // if there is an error with drawing a point or the line is finished get out of the loop!
            while (!((x1 == x2 && y1 == y2) || !DrawPoint(x1, y1, isOn)))
            {
                e2 = 2 * err;

                if (e2 > -dy)
                {
                    err -= dy;
                    x1 += sx;
                }

                if (e2 < dx)
                {
                    err += dx;
                    y1 += sy;
                }
            }
        }

        /// <summary>
        /// Draw a line.
        /// </summary>
        /// <param name="p1">First point coordinate.</param>
        /// <param name="p2">Second point coordinate.</param>
        /// <param name="isOn">True if the line has pixels on, false for off.</param>
        public void DrawLine(Point p1, Point p2, bool isOn) => DrawLine(p1.X, p1.Y, p2.X, p2.Y, isOn);

        /// <summary>
        /// Draw a rectangle.
        /// </summary>
        /// <param name="x">The X coordinate.</param>
        /// <param name="y">The Y coordinate.</param>
        /// <param name="width">The width of the rectangle.</param>
        /// <param name="height">The height of the rectangle.</param>
        /// <param name="isOn">True if the rectangle has pixels on, false for off.</param>
        /// <param name="isFilled">If it's filled or not.</param>
        public void DrawRectangle(int x, int y, int width, int height, bool isOn, bool isFilled)
        {
            // This will draw points
            int xe = x + width;
            int ye = y + height;

            if (isFilled)
            {
                for (int yy = y; yy != ye; yy++)
                {
                    for (int xx = x; xx != xe; xx++)
                    {
                        DrawPoint(xx, yy, isOn);
                    }
                }
            }
            else
            {
                xe -= 1;
                ye -= 1;

                for (int xx = x; xx != xe; xx++)
                {
                    DrawPoint(xx, y, isOn);
                }

                for (int xx = x; xx <= xe; xx++)
                {
                    DrawPoint(xx, ye, isOn);
                }

                for (int yy = y; yy != ye; yy++)
                {
                    DrawPoint(x, yy, isOn);
                }

                for (int yy = y; yy <= ye; yy++)
                {
                    DrawPoint(xe, yy, isOn);
                }
            }
        }

        /// <summary>
        /// Draw a rectangle.
        /// </summary>
        /// <param name="p">The coordinate of the point.</param>
        /// <param name="size">The size of the rectangle.</param>
        /// <param name="isOn">True if the rectangle has pixels on, false for off.</param>
        /// <param name="isFilled">If it's filled or not.</param>
        public void DrawRectangle(Point p, Size size, bool isOn, bool isFilled) => DrawRectangle(p.X, p.Y, size.Width, size.Height, isOn, isFilled);

        /// <summary>
        /// Draw a rectangle.
        /// </summary>
        /// <param name="rectangle">The rectangle.</param>
        /// <param name="isOn">True if the rectangle has pixels on, false for off.</param>
        /// <param name="isFilled">If it's filled or not.</param>
        public void DrawRectangle(Rectangle rectangle, bool isOn, bool isFilled) => DrawRectangle(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, isOn, isFilled);

        #endregion

        private void SpiWrite(bool isData, SpanByte toSend)
        {
            _controller.Write(_dataCommandPin, isData ? PinValue.High : PinValue.Low);
            _spiDevice.Write(toSend);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_shouldDispose)
            {
                _controller?.Dispose();
                _controller = null;
            }
            else
            {
                if (_controller is object)
                {
                    if (_controller.IsPinOpen(_dataCommandPin))
                    {
                        _controller.ClosePin(_dataCommandPin);
                    }

                    if (_controller.IsPinOpen(_resetPin))
                    {
                        _controller.ClosePin(_resetPin);
                    }

                    if (_controller.IsPinOpen(_backlightPin))
                    {
                        _controller.ClosePin(_backlightPin);
                    }
                }
            }

            _spiDevice?.Dispose();
            _spiDevice = null;
            _pwmBacklight?.Dispose();
            _pwmBacklight = null;
        }
    }

    [Flags]
    internal enum FunctionSet
    {
        PowerOn = 0b0010_0000,
        PowerOff = 0b0010_0100,
        ExtendedMode = 0b0010_0001,
        HorizontalAddressing = 0b0010_0000,
        VerticalAddressing = 0b0010_0010,
    }

    internal enum SetAddress
    {
        YAddress = 0b0100_0000,
        XAddress = 0b1000_0000,
    }

    internal enum PcdDisplayControl
    {
        DisplayBlank = 0b0000_1000,
        NormalMode = 0b0000_1100,
        AllSegmentsOn = 0b0000_1001,
        InverseVideoMode = 0b0000_1101,
    }

    public enum ScreenTemperature
    {
        /// <summary>Temperature Coefficient 0.</summary>
        Coefficient0 = 0b0000_0100,

        /// <summary>Temperature Coefficient 1.</summary>
        Coefficient1 = 0b0000_0101,

        /// <summary>Temperature Coefficient 2.</summary>
        Coefficient2 = 0b0000_0110,

        /// <summary>Temperature Coefficient 3.</summary>
        Coefficient3 = 0b0000_0111,
    }

    public class Font5x8 : BdfFont
    {
        //
        // Summary:
        //     ASCII Font specific to the PCD8544 Nokia 5110 screen but can be used as a generic
        //     5x8 font. Font characters are column bit mask. Font size is 5 pixels width and
        //     8 pixels height. Each byte represent a vertical column for the character.
        private static byte[][] Ascii() => new byte[96][]
        {
        new byte[5],
        new byte[5] { 0, 0, 95, 0, 0 },
        new byte[5] { 0, 7, 0, 7, 0 },
        new byte[5] { 20, 127, 20, 127, 20 },
        new byte[5] { 36, 42, 127, 42, 18 },
        new byte[5] { 35, 19, 8, 100, 98 },
        new byte[5] { 54, 73, 85, 34, 80 },
        new byte[5] { 0, 5, 3, 0, 0 },
        new byte[5] { 0, 28, 34, 65, 0 },
        new byte[5] { 0, 65, 34, 28, 0 },
        new byte[5] { 20, 8, 62, 8, 20 },
        new byte[5] { 8, 8, 62, 8, 8 },
        new byte[5] { 0, 80, 48, 0, 0 },
        new byte[5] { 8, 8, 8, 8, 8 },
        new byte[5] { 0, 96, 96, 0, 0 },
        new byte[5] { 32, 16, 8, 4, 2 },
        new byte[5] { 62, 81, 73, 69, 62 },
        new byte[5] { 0, 66, 127, 64, 0 },
        new byte[5] { 66, 97, 81, 73, 70 },
        new byte[5] { 33, 65, 69, 75, 49 },
        new byte[5] { 24, 20, 18, 127, 16 },
        new byte[5] { 39, 69, 69, 69, 57 },
        new byte[5] { 60, 74, 73, 73, 48 },
        new byte[5] { 1, 113, 9, 5, 3 },
        new byte[5] { 54, 73, 73, 73, 54 },
        new byte[5] { 6, 73, 73, 41, 30 },
        new byte[5] { 0, 54, 54, 0, 0 },
        new byte[5] { 0, 86, 54, 0, 0 },
        new byte[5] { 8, 20, 34, 65, 0 },
        new byte[5] { 20, 20, 20, 20, 20 },
        new byte[5] { 0, 65, 34, 20, 8 },
        new byte[5] { 2, 1, 81, 9, 6 },
        new byte[5] { 50, 73, 121, 65, 62 },
        new byte[5] { 126, 17, 17, 17, 126 },
        new byte[5] { 127, 73, 73, 73, 54 },
        new byte[5] { 62, 65, 65, 65, 34 },
        new byte[5] { 127, 65, 65, 34, 28 },
        new byte[5] { 127, 73, 73, 73, 65 },
        new byte[5] { 127, 9, 9, 9, 1 },
        new byte[5] { 62, 65, 73, 73, 122 },
        new byte[5] { 127, 8, 8, 8, 127 },
        new byte[5] { 0, 65, 127, 65, 0 },
        new byte[5] { 32, 64, 65, 63, 1 },
        new byte[5] { 127, 8, 20, 34, 65 },
        new byte[5] { 127, 64, 64, 64, 64 },
        new byte[5] { 127, 2, 12, 2, 127 },
        new byte[5] { 127, 4, 8, 16, 127 },
        new byte[5] { 62, 65, 65, 65, 62 },
        new byte[5] { 127, 9, 9, 9, 6 },
        new byte[5] { 62, 65, 81, 33, 94 },
        new byte[5] { 127, 9, 25, 41, 70 },
        new byte[5] { 70, 73, 73, 73, 49 },
        new byte[5] { 1, 1, 127, 1, 1 },
        new byte[5] { 63, 64, 64, 64, 63 },
        new byte[5] { 31, 32, 64, 32, 31 },
        new byte[5] { 63, 64, 56, 64, 63 },
        new byte[5] { 99, 20, 8, 20, 99 },
        new byte[5] { 7, 8, 112, 8, 7 },
        new byte[5] { 97, 81, 73, 69, 67 },
        new byte[5] { 0, 127, 65, 65, 0 },
        new byte[5] { 2, 4, 8, 16, 32 },
        new byte[5] { 0, 65, 65, 127, 0 },
        new byte[5] { 4, 2, 1, 2, 4 },
        new byte[5] { 64, 64, 64, 64, 64 },
        new byte[5] { 0, 1, 2, 4, 0 },
        new byte[5] { 32, 84, 84, 84, 120 },
        new byte[5] { 127, 72, 68, 68, 56 },
        new byte[5] { 56, 68, 68, 68, 32 },
        new byte[5] { 56, 68, 68, 72, 127 },
        new byte[5] { 56, 84, 84, 84, 24 },
        new byte[5] { 8, 126, 9, 1, 2 },
        new byte[5] { 12, 82, 82, 82, 62 },
        new byte[5] { 127, 8, 4, 4, 120 },
        new byte[5] { 0, 68, 125, 64, 0 },
        new byte[5] { 32, 64, 68, 61, 0 },
        new byte[5] { 127, 16, 40, 68, 0 },
        new byte[5] { 0, 65, 127, 64, 0 },
        new byte[5] { 124, 4, 24, 4, 120 },
        new byte[5] { 124, 8, 4, 4, 120 },
        new byte[5] { 56, 68, 68, 68, 56 },
        new byte[5] { 124, 20, 20, 20, 8 },
        new byte[5] { 8, 20, 20, 24, 124 },
        new byte[5] { 124, 8, 4, 4, 8 },
        new byte[5] { 72, 84, 84, 84, 32 },
        new byte[5] { 4, 63, 68, 64, 32 },
        new byte[5] { 60, 64, 64, 32, 124 },
        new byte[5] { 28, 32, 64, 32, 28 },
        new byte[5] { 60, 64, 48, 64, 60 },
        new byte[5] { 68, 40, 16, 40, 68 },
        new byte[5] { 12, 80, 80, 80, 60 },
        new byte[5] { 68, 100, 84, 76, 68 },
        new byte[5] { 0, 8, 54, 65, 0 },
        new byte[5] { 0, 0, 127, 0, 0 },
        new byte[5] { 0, 65, 54, 8, 0 },
        new byte[5] { 16, 8, 8, 16, 8 },
        new byte[5] { 120, 70, 65, 70, 120 }
        };

        //
        // Summary:
        //     Constructor for Font 5x8
        public Font5x8()
        {
            var ascii = Ascii();
            base.Width = 5;
            base.Height = 8;
            base.XDisplacement = 0;
            base.YDisplacement = 0;
            base.DefaultChar = 32;
            base.CharsCount = ascii.Length;
            base.GlyphMapper = new Hashtable();
            base.GlyphUshortData = new ushort[base.CharsCount * base.Height];
            for (int i = 0; i < base.CharsCount; i++)
            {
                byte[] array = LcdCharacterEncodingFactory.ConvertFont5to8bytes(ascii[i]);
                for (int j = 0; j < 8; j++)
                {
                    base.GlyphUshortData[i * 8 + j] = array[j];
                }

                base.GlyphMapper.Add(i + base.DefaultChar, i * 8);
            }
        }
    }
}
