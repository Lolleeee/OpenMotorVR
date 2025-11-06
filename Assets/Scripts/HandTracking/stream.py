import cv2
import mediapipe as mp
import socket
import json
import numpy as np
from collections import deque
import csv
import time

# MediaPipe setup
mp_hands = mp.solutions.hands
mp_drawing = mp.solutions.drawing_utils
hands = mp_hands.Hands(static_image_mode=False, max_num_hands=2)

# UDP Socket
UDP_IP = "127.0.0.1"
UDP_PORT = 5065
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

# Trajectory tracking
trajectory_buffer = {
    "Left": deque(maxlen=100),  # Store last 100 positions
    "Right": deque(maxlen=100)
}

# Data logging
csv_file = open('hand_trajectories.csv', 'w', newline='')
csv_writer = csv.writer(csv_file)
csv_writer.writerow(['timestamp', 'hand', 'landmark_id', 'x', 'y', 'z', 
                     'velocity_x', 'velocity_y', 'velocity_z'])

cap = cv2.VideoCapture(0)
prev_time = time.time()
prev_positions = {}

while cap.isOpened():
    success, frame = cap.read()
    if not success:
        continue
    
    frame = cv2.flip(frame, 1)
    rgb_frame = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
    results = hands.process(rgb_frame)
    
    current_time = time.time()
    dt = current_time - prev_time
    
    hand_data = {"hands": []}
    
    if results.multi_hand_landmarks and results.multi_handedness:
        for hand_landmarks, handedness in zip(results.multi_hand_landmarks, 
                                                results.multi_handedness):
            label = handedness.classification[0].label
            hand_info = {"label": label, "landmarks": []}
            
            mp_drawing.draw_landmarks(
                frame,
                hand_landmarks,
                mp_hands.HAND_CONNECTIONS
            )

            for idx, landmark in enumerate(hand_landmarks.landmark):
                pos = np.array([landmark.x, landmark.y, landmark.z])
                
                # Calculate velocity
                key = f"{label}_{idx}"
                if key in prev_positions and dt > 0:
                    velocity = (pos - prev_positions[key]) / dt
                else:
                    velocity = np.array([0, 0, 0])
                
                prev_positions[key] = pos
                
                # Log trajectory data
                csv_writer.writerow([
                    current_time, label, idx,
                    pos[0], pos[1], pos[2],
                    velocity[0], velocity[1], velocity[2]
                ])
                
                hand_info["landmarks"].append({
                    "x": float(pos[0]),
                    "y": float(pos[1]),
                    "z": float(pos[2])
                })
            
            # Track wrist trajectory for visualization
            wrist_pos = np.array([hand_landmarks.landmark[0].x,
                                 hand_landmarks.landmark[0].y,
                                 hand_landmarks.landmark[0].z])
            trajectory_buffer[label].append(wrist_pos)
            
            # Calculate trajectory metrics
            if len(trajectory_buffer[label]) > 1:
                trajectory = np.array(trajectory_buffer[label])
                path_length = np.sum(np.linalg.norm(np.diff(trajectory, axis=0), axis=1))
                avg_speed = path_length / (len(trajectory) * dt) if dt > 0 else 0
                print(f"{label} hand - Path length: {path_length:.3f}, Avg speed: {avg_speed:.3f}")
            
            hand_data["hands"].append(hand_info)
            
    # Send to Unity
    message = json.dumps(hand_data).encode('utf-8')
    sock.sendto(message, (UDP_IP, UDP_PORT))
    print(f"Sent {len(message)} bytes with {len(hand_data['hands'])} hands")
    prev_time = current_time
    
    cv2.imshow('MediaPipe Hand Tracking', frame)
    if cv2.waitKey(1) & 0xFF == ord('q'):
        break

cap.release()
cv2.destroyAllWindows()
sock.close()
csv_file.close()
