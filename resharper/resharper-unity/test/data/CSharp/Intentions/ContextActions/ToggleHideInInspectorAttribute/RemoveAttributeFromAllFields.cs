using UnityEngine;

public class Foo : MonoBehaviour
{
  [HideInInspector] [SerializeField] private int my{caret:Remove:'HideInInspector':from:all:fields}Value, myValue2, myValue3;
}
