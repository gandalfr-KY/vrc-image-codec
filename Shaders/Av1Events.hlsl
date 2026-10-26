// Packed syntax-event contract shared by generation and adaptive probability evaluation.
// Bits 0..11: context, 12..15: symbol, 16..19: threshold count, 20..21: event kind.
static const int AdaptiveEvent = 0;
static const int BottomEdgeEvent = 1;
static const int RightEdgeEvent = 2;
static const int UniformEvent = 3;
static const int UniformContext = 4095;

uint PackEvent(int context, int symbol, int length, int kind)
{
    return (uint)(context | (symbol << 12) | (length << 16) | (kind << 20));
}
