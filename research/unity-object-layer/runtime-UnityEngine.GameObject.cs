using System;
using System.Runtime.CompilerServices;
using UnityEngineInternal;

namespace UnityEngine;

public sealed class GameObject : Object
{
	public extern bool isStatic
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		set;
	}

	internal extern bool isStaticBatchable
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern Transform transform
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern Rigidbody rigidbody
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern Camera camera
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern Light light
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern Animation animation
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern ConstantForce constantForce
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern Renderer renderer
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern AudioSource audio
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern GUIText guiText
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern NetworkView networkView
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	[Obsolete("Please use guiTexture instead")]
	public extern GUIElement guiElement
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern GUITexture guiTexture
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern Collider collider
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern HingeJoint hingeJoint
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern ParticleEmitter particleEmitter
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern ParticleSystem particleSystem
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern int layer
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		set;
	}

	[Obsolete("GameObject.active is obsolete. Use GameObject.SetActive(), GameObject.activeSelf or GameObject.activeInHierarchy.")]
	public extern bool active
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		set;
	}

	public extern bool activeSelf
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern bool activeInHierarchy
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
	}

	public extern string tag
	{
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		get;
		[MethodImpl(MethodImplOptions.InternalCall)]
		[WrapperlessIcall]
		set;
	}

	public GameObject gameObject => this;

	public GameObject(string name)
	{
		Internal_CreateGameObject(this, name);
	}

	public GameObject()
	{
		Internal_CreateGameObject(this, null);
	}

	public GameObject(string name, params Type[] components)
	{
		Internal_CreateGameObject(this, name);
		foreach (Type componentType in components)
		{
			AddComponent(componentType);
		}
	}

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	public static extern GameObject CreatePrimitive(PrimitiveType type);

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	[TypeInferenceRule(TypeInferenceRules.TypeReferencedByFirstArgument)]
	public extern Component GetComponent(Type type);

	public T GetComponent<T>() where T : Component
	{
		return GetComponent(typeof(T)) as T;
	}

	public Component GetComponent(string type)
	{
		return GetComponentByName(type);
	}

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	private extern Component GetComponentByName(string type);

	[TypeInferenceRule(TypeInferenceRules.TypeReferencedByFirstArgument)]
	public Component GetComponentInChildren(Type type)
	{
		if (activeInHierarchy)
		{
			Component component = GetComponent(type);
			if (component != null)
			{
				return component;
			}
		}
		Transform transform = this.transform;
		if (transform != null)
		{
			foreach (Transform item in transform)
			{
				Component componentInChildren = item.gameObject.GetComponentInChildren(type);
				if (componentInChildren != null)
				{
					return componentInChildren;
				}
			}
		}
		return null;
	}

	public T GetComponentInChildren<T>() where T : Component
	{
		return GetComponentInChildren(typeof(T)) as T;
	}

	[CanConvertToFlash]
	public Component[] GetComponents(Type type)
	{
		return GetComponentsInternal(type, isGenericTypeArray: false, recursive: false, includeInactive: true);
	}

	public T[] GetComponents<T>() where T : Component
	{
		return (T[])GetComponentsInternal(typeof(T), isGenericTypeArray: true, recursive: false, includeInactive: true);
	}

	public Component[] GetComponentsInChildren(Type type)
	{
		bool includeInactive = false;
		return GetComponentsInChildren(type, includeInactive);
	}

	public Component[] GetComponentsInChildren(Type type, bool includeInactive)
	{
		return GetComponentsInternal(type, isGenericTypeArray: false, recursive: true, includeInactive);
	}

	public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component
	{
		return (T[])GetComponentsInternal(typeof(T), isGenericTypeArray: true, recursive: true, includeInactive);
	}

	public T[] GetComponentsInChildren<T>() where T : Component
	{
		return GetComponentsInChildren<T>(includeInactive: false);
	}

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	private extern Component[] GetComponentsInternal(Type type, bool isGenericTypeArray, bool recursive, bool includeInactive);

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	public extern void SetActive(bool value);

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	[Obsolete("gameObject.SetActiveRecursively() is obsolete. Use GameObject.SetActive(), which is now inherited by children.")]
	public extern void SetActiveRecursively(bool state);

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	public extern bool CompareTag(string tag);

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	public static extern GameObject FindGameObjectWithTag(string tag);

	public static GameObject FindWithTag(string tag)
	{
		return FindGameObjectWithTag(tag);
	}

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	public static extern GameObject[] FindGameObjectsWithTag(string tag);

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	public extern void SendMessageUpwards(string methodName, object value, SendMessageOptions options);

	public void SendMessageUpwards(string methodName, object value)
	{
		SendMessageOptions options = SendMessageOptions.RequireReceiver;
		SendMessageUpwards(methodName, value, options);
	}

	public void SendMessageUpwards(string methodName)
	{
		SendMessageOptions options = SendMessageOptions.RequireReceiver;
		object value = null;
		SendMessageUpwards(methodName, value, options);
	}

	public void SendMessageUpwards(string methodName, SendMessageOptions options)
	{
		SendMessageUpwards(methodName, null, options);
	}

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	public extern void SendMessage(string methodName, object value, SendMessageOptions options);

	public void SendMessage(string methodName, object value)
	{
		SendMessageOptions options = SendMessageOptions.RequireReceiver;
		SendMessage(methodName, value, options);
	}

	public void SendMessage(string methodName)
	{
		SendMessageOptions options = SendMessageOptions.RequireReceiver;
		object value = null;
		SendMessage(methodName, value, options);
	}

	public void SendMessage(string methodName, SendMessageOptions options)
	{
		SendMessage(methodName, null, options);
	}

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	public extern void BroadcastMessage(string methodName, object parameter, SendMessageOptions options);

	public void BroadcastMessage(string methodName, object parameter)
	{
		SendMessageOptions options = SendMessageOptions.RequireReceiver;
		BroadcastMessage(methodName, parameter, options);
	}

	public void BroadcastMessage(string methodName)
	{
		SendMessageOptions options = SendMessageOptions.RequireReceiver;
		object parameter = null;
		BroadcastMessage(methodName, parameter, options);
	}

	public void BroadcastMessage(string methodName, SendMessageOptions options)
	{
		BroadcastMessage(methodName, null, options);
	}

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	public extern Component AddComponent(string className);

	[TypeInferenceRule(TypeInferenceRules.TypeReferencedByFirstArgument)]
	public Component AddComponent(Type componentType)
	{
		return Internal_AddComponentWithType(componentType);
	}

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	private extern Component Internal_AddComponentWithType(Type componentType);

	public T AddComponent<T>() where T : Component
	{
		return AddComponent(typeof(T)) as T;
	}

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	private static extern void Internal_CreateGameObject([Writable] GameObject mono, string name);

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	public extern void SampleAnimation(AnimationClip animation, float time);

	[MethodImpl(MethodImplOptions.InternalCall)]
	[Obsolete("gameObject.PlayAnimation is not supported anymore. Use animation.Play")]
	[WrapperlessIcall]
	public extern void PlayAnimation(AnimationClip animation);

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	[Obsolete("gameObject.StopAnimation is not supported anymore. Use animation.Stop")]
	public extern void StopAnimation();

	[MethodImpl(MethodImplOptions.InternalCall)]
	[WrapperlessIcall]
	public static extern GameObject Find(string name);
}
