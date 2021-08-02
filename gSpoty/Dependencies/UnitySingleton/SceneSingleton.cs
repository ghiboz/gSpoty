using System.Collections;
using System.Collections.Generic;

// https://answers.unity.com/questions/1408574/destroying-and-recreating-a-singleton.html
public class SceneSingleton<T> where T : class, new()
{
    private static T m_Instance = null;
    public static T Instance
    {
        get
        {
            if (m_Instance == null)
            {
                m_Instance = new T();
            }
            return m_Instance;
        }
    }
}