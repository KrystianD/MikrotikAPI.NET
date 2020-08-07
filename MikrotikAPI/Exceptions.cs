using System;

namespace MikrotikAPI
{
  public class MikrotikConnectionException : Exception { }

  public class MikrotikAlreadyConnectedException : Exception { }

  public class MikrotikNotConnectedException : Exception { }

  public class MikrotikInvalidResponseException : Exception { }

  public class MikrotikFatalException : Exception
  {
    public MikrotikFatalException(string message) : base(message) { }
  }

  public class MikrotikTrapException : Exception { }

  public class MikrotikInvalidCredentialsException : Exception { }

  public class MikrotikInternalException : Exception { }
}