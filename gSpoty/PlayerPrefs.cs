using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace gSpoty
{
    public static class PlayerPrefs
    {
        public static List<string> item = new List<string>();
        public static List<string> value = new List<string>();


        public static string GetString(string s)
        {
            var id = item.IndexOf(s);
            if (id >= 0)
            {
                return value[id];
            }

            return string.Empty;
        }

        public static void SetString(string s, string v)
        {
            var id = item.IndexOf(s);
            if (id >= 0)
            {
                value[id] = v;
            }
            else
            {
                item.Add(s);
                item.Add(v);
            }

        }
    }
}
