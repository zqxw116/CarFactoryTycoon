using System;
using UnityEngine;

/// <summary>
/// 일반 싱글톤.
/// </summary>
public class Singleton<T> where T : class, new()
{
    private static readonly Lazy<T> instance = new Lazy<T>(() => new T());
    public static T Instance { get { return instance.Value; } }
    protected Singleton() { }

    public virtual void Init() { }
}


/// <summary>
/// 해당클래스를 가지고 있는 이미 만들어진 게임오브젝트를 싱글톤으로 반환.
/// </summary>
public class GameObjectSingleton<T> : MonoBehaviour where T : MonoBehaviour
{
    private static T instance;
    public static T Instance { get { if (instance == null) { instance = GameObject.FindFirstObjectByType<T>(); } return instance; } }
    public virtual void Init() { }
}

/// <summary>
/// 새 게임오브젝트를 클래스 이름으로 생성하고 해당 클래스를 붙여 싱글톤으로 반환.
/// </summary>
public class MonoSingleton<T> : MonoBehaviour where T : MonoBehaviour
{
    private static T instance;
    public static T Instance { get { if (instance == null) {
                GameObject obj = new GameObject(typeof(T).ToString());
                obj.transform.localScale = Vector3.one;
                obj.transform.localPosition = Vector3.zero;
                obj.transform.localRotation = Quaternion.identity;
                instance = obj.AddComponent<T>(); } return instance; }
    }
    public virtual void Init() { }
}

