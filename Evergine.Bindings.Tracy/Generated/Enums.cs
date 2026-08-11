using System;

namespace Evergine.Bindings.Tracy
{
	public enum TracyPlotFormatEnum
	{
		TracyPlotFormatNumber = 0,
		TracyPlotFormatMemory = 1,
		TracyPlotFormatPercentage = 2,
		TracyPlotFormatWatt = 3,
	}

	public enum TracyMessageSeverity
	{
		TracyMessageSeverityTrace = 0,
		TracyMessageSeverityDebug = 1,
		TracyMessageSeverityInfo = 2,
		TracyMessageSeverityWarning = 3,
		TracyMessageSeverityError = 4,
		TracyMessageSeverityFatal = 5,
	}
}
