namespace Hmi.Comms.Core;

/// <summary>
/// One physical value on one device connection, shared by every tag -- in
/// every session -- that addresses it. The worker reads it; listeners hear
/// about changes.
/// </summary>
public sealed class Point
{
	private readonly Dictionary<object, int> subscriptions = new();
	private readonly List<IPointListener> listeners = new();

	internal Point(ParsedAddress address, int index)
	{
		Address = address;
		Index = index;
		Value = TagValue.Bad(Status.Waiting, null, 0);
	}

	public ParsedAddress Address { get; }

	/// <summary>Stable within its worker; used to key read plans.</summary>
	public int Index { get; }

	/// <summary>The latest raw value. Written only by the worker thread.</summary>
	public TagValue Value { get; internal set; }

	/// <summary>Scratch space for the driver, e.g. a learned type or size.</summary>
	public object? DriverState { get; set; }

	/// <summary>Poll period: the fastest any subscriber asked for; 0 when nobody is subscribed.</summary>
	public int RateMs { get; private set; }

	internal long NextDue { get; set; }

	/// <summary>Number of tags referencing this point; it is dropped at zero.</summary>
	internal int RefCount { get; set; }

	internal void SetSubscription(object subscriber, int rateMs)
	{
		if (rateMs > 0)
		{
			subscriptions[subscriber] = rateMs;
		}
		else
		{
			subscriptions.Remove(subscriber);
		}

		RateMs = subscriptions.Count == 0 ? 0 : subscriptions.Values.Min();
	}

	internal void AddListener(IPointListener l)
	{
		lock (listeners)
		{
			if (!listeners.Contains(l))
			{
				listeners.Add(l);
			}
		}
	}

	internal void RemoveListener(IPointListener l)
	{
		lock (listeners)
		{
			listeners.Remove(l);
		}
	}

	internal IPointListener[] Listeners()
	{
		lock (listeners)
		{
			return listeners.ToArray();
		}
	}

	public override string ToString() => Address.Normalized;
}

public interface IPointListener
{
	void OnPointChanged(Point point);
}
