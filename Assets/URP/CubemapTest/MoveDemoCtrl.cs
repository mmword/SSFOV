using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MoveDemoCtrl : MonoBehaviour
{

    public float speed = 1f;

#if UNITY_STANDALONE || UNITY_EDITOR

    void MoveToPoint()
    {
        if (Input.GetMouseButton(0))
        {
            var mPos = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            var screen = new Vector2(Screen.width/2,Screen.height/2);
            var dir = (mPos - screen).normalized;
            transform.position += new Vector3(dir.x * speed, 0, dir.y * speed);
        }
    }

#else

    void MoveToPoint()
    {
        if (Input.touchCount > 0)
        {
            var t0 = Input.GetTouch(0);
            if (t0.phase == TouchPhase.Moved || t0.phase == TouchPhase.Stationary)
            {
                var screen = new Vector2(Screen.width / 2, Screen.height / 2);
                var dir = (t0.position - screen).normalized;
                transform.position += new Vector3(dir.x * speed, 0, dir.y * speed);
            }
        }
    }

#endif

    // Update is called once per frame
    void FixedUpdate()
    {
        MoveToPoint();
    }
}
