using System.Collections;
using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;

public class NewBehaviourScript : MonoBehaviour
{
    public int x;
    void Update2([CanBeNull]NewBehaviourScript script)
    {
        if (script && script.x == 0)
        {
             
        }
    }
}