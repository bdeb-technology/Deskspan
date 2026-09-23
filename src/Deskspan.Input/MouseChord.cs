namespace Deskspan.Input;

public static class MouseChord
{
    public static ushort Button(int message, uint mouseData, out bool down)
    {
        down = false;
        switch (message)
        {
            case 0x0201:
                down = true;
                return 0x01;
            case 0x0202:
                return 0x01;
            case 0x0204:
                down = true;
                return 0x02;
            case 0x0205:
                return 0x02;
            case 0x0207:
                down = true;
                return 0x04;
            case 0x0208:
                return 0x04;
            case 0x020B:
            case 0x020C:
                down = message == 0x020B;
                return (mouseData >> 16) == 2 ? (ushort)0x06 : (ushort)0x05;
            default:
                return 0;
        }
    }
}
