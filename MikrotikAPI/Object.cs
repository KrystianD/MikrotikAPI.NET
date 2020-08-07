using System.Collections.Generic;

namespace MikrotikAPI
{
  public class Object
  {
    public readonly Dictionary<string, string> Attributes;

    public Object(Dictionary<string, string> attributes)
    {
      Attributes = attributes;
    }
  }
}