import random
import json
import requests
import asyncio
import websockets
from flask import Flask, jsonify, request, Response
import os
import time
import sqlite3
import base64

app = Flask(__name__)

def init_db():
    conn = sqlite3.connect('scores.db')
    cursor = conn.cursor()
    cursor.execute('''
        CREATE TABLE IF NOT EXISTS users (
            username TEXT PRIMARY KEY,
            total_score INTEGER,
            image_url TEXT
        )
    ''')
    conn.commit()
    conn.close()

init_db()

themes = [
    "animals", "cars", "cooking", "music", "sports", "nature", "travel", "technology",
    "art", "science", "space", "society", "economy", "history", "geography", "weather",
    "health", "education", "politics", "hobbies", "games", "fashion", "film", "literature",
    "archaeology", "computing", "mathematics", "photography", "ecology", "architecture",
    "biology", "psychology", "philosophy", "mythology", "religion", "media", "transport",
    "lifestyle", "crafts", "finance", "entrepreneurship", "communication", "cinema",
    "museums", "sculpture", "sustainability", "emerging technologies", "robotics",
    "astrology", "marine life", "urban development", "cultural heritage", "social media",
    "personal development", "virtual reality", "augmented reality", "genetics", "climate change",
    "innovation", "space exploration", "quantum computing", "cybersecurity", "game design",
    "biotechnology", "robotic process automation", "artificial intelligence", "machine learning",
    "data science", "internet of things", "blockchain", "digital marketing", "web development",
    "software engineering", "hardware design", "automation", "renewable energy", "sustainable living",
    "green technology", "food science", "material science", "nuclear physics", "astrophysics",
    "neuroscience", "public health", "medieval history", "ancient civilizations", "sociology",
    "social justice", "human rights", "philanthropy", "community service", "environmental activism",
    "cognitive science", "human-computer interaction", "e-learning", "online education", "robotics ethics"
]

GROQ_API_KEY = os.environ.get("GROQ_API_KEY", "<APIKEY>")

@app.route('/', methods=['GET'])
def get_exercise():

    theme = random.choice(themes)
    
    url = "https://api.groq.com/openai/v1/chat/completions"
    headers = {
        "Content-Type": "application/json",
        "Authorization": f"Bearer {GROQ_API_KEY}"
    }
    data = {
        "messages": [
            {
                "role": "user",
                "content": f"""You are an AI assistant tasked with creating a simple Python exercise based on a given theme. Your goal is to design an exercise suitable for beginners or intermediate learners, focusing on the provided theme. Follow these instructions carefully to create the exercise:

1. Read the following theme:
<theme>
{theme}
</theme>

2. Create a Python exercise related to the theme. The exercise should:
   - Be appropriate for beginners or intermediate learners
   - Focus on a specific Python concept or skill
   - Include clear instructions for the user
   - Have a maximum duration of 5 minutes or 12 minutes
   - Use only input() for data entry, without any text inside the parentheses
   - Avoid exercises involving complex calculations or very precise output
   - Use only very simple math that can be done mentally with round numbers

3. Write the exercise description in markdown format, including:
   - A brief introduction to the problem
   - Input format explanation (keyboard input)
   - Output format explanation
   - Any constraints or limitations
   - An example of input and output

4. Provide starter code if necessary. The starter code must:
   - Contain only comments
   - There is only **one** `input()` and `print()` (text keyboard input) in the starter code
   - Not include any functional code
   - Only include placeholders for `print()` and `input()` functions
   - If the user needs to use a predefined array/list in their code, it is imperative to provide them with the array in the starter code

5. Create 5 examples of input/output pairs for the exercise. Format these examples as a JSON object with "input" and "output" keys for each example. Ensure that:
   - There is only one input() (text keyboard input) for each output
   - Multiple inputs are separated by commas if necessary
   - No '\\n' characters are used in the input

6. Estimate the time it would take to complete this exercise in minutes.

Present your exercise and examples in the following format:

<exercise>
[Insert your exercise description here in markdown, following the structure outlined in step 3]
</exercise>

<starter_code>
[Insert starter code here if applicable, otherwise leave this section empty]
</starter_code>

<examples>
[Insert the JSON **in one line** object containing 5 input/output examples here (json in format : {{ "examples": [ {{ "input" .........]])]
</examples>

<challenge_time>
[Insert your estimated time here in minutes]
</challenge_time>

Ensure that your exercise is clear, concise, and aligned with the given theme. The examples should cover a range of possible inputs and their corresponding correct outputs."""
            }
        ],
        "model": "llama-3.1-70b-versatile",
        "temperature": 0.5,
        "max_tokens": 8000,
        "top_p": 1,
        "stream": False,
        "stop": None
    }

    for attempt in range(3): 
        try:

            response = requests.post(url, headers=headers, json=data)

            if response.status_code == 200:
                content = response.json()['choices'][0]['message']['content']

                exercise = content.split("<exercise>")[1].split("</exercise>")[0].strip()
                starter_code = content.split("<starter_code>")[1].split("</starter_code>")[0].strip()
                examples = content.split("<examples>")[1].split("</examples>")[0].strip()
                challenge_time = content.split("<challenge_time>")[1].split("</challenge_time>")[0].strip()

                starter_code = starter_code.strip('`python').strip('`')

                try:
                    examples_json = json.loads(examples)
                    examples_str = json.dumps({"examples": examples_json["examples"]}, indent=2)
                except json.JSONDecodeError:
                    examples_str = examples

                result = {
                    "challenge_time": challenge_time,
                    "examples": {
                        "examples": examples_json["examples"]
                    },
                    "exercise": exercise,
                    "starter_code": starter_code
                }

                return jsonify(result)

            else:
                raise Exception("API response error")

        except (requests.RequestException, json.JSONDecodeError, IndexError) as e:
            print(f"Attempt {attempt + 1} failed: {e}")
            time.sleep(1)

    return jsonify({"error": "Failed to generate exercise after multiple attempts"}), 500


@app.route('/tests', methods=['POST'])
def run_tests():
    data = request.json
    code = data.get('code')
    tests = data.get('tests')

    if not code or not tests:
        return jsonify({"error": "Missing code or tests"}), 400

    def generate():
        for test in tests:
            input_data = test['input']
            expected_output = str(test['output'])

            actual_output = run_code(code, input_data)

            is_correct = actual_output.strip() == expected_output.strip()
    
            result = {
                "input": input_data,
                "expected_output": expected_output,
                "actual_output": actual_output,
                "is_correct": is_correct
            }
    
            yield f"data: {json.dumps(result)}\n\n"

    return Response(generate(), content_type='text/event-stream')

def run_code(code, input_data):
    response = requests.post("https://tpm28.tech/ces/run_interactive", json={
        "lang": "python",
        "code": code
    })

    if response.status_code != 200:
        return f"Error: Failed to run code (status {response.status_code})"

    session_id = response.json().get('id')

    if not session_id:
        return "Error: No session ID received"

    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    output = loop.run_until_complete(interact_with_websocket(session_id, input_data))
    loop.close()

    return output

async def interact_with_websocket(session_id, input_data):
    uri = f"wss://tpm28.tech/ces/ws"
    async with websockets.connect(uri) as websocket:
        await websocket.send(json.dumps({"id": session_id}))

        if not isinstance(input_data, str):
            input_data = str(input_data)

        await websocket.send(input_data)

        output = ""
        try:
            while True:
                message = await asyncio.wait_for(websocket.recv(), timeout=2.0)
                if message == "Python program has finished execution.":
                    break
                output += message + "\n"
        except asyncio.TimeoutError:
            output = "Timeout: No response received"

    return output.strip()


@app.route('/eval', methods=['POST'])
def evaluate_code():
    data = request.json
    username = data.get('username')
    consigne = data.get('consigne')
    code = data.get('code')
    temps_code = data.get('temps_code')

    if not username or not consigne or not code or not temps_code:
        return jsonify({"error": "Missing username, consigne, code, or temps_code"}), 400

    url = "https://api.groq.com/openai/v1/chat/completions"
    headers = {
        "Content-Type": "application/json",
        "Authorization": f"Bearer {GROQ_API_KEY}"
    }
    payload = {
        "messages": [
            {
                "role": "user",
                "content": f"""You are an AI assistant tasked with evaluating Python code based on given instructions and time spent coding. You will assign an XP score with a maximum of 500 points. The score should reflect the code quality, efficiency, and appropriateness for the given time frame.

You will be provided with three inputs:
1. A consigne (instruction) in French
2. A Python code snippet
3. The time spent coding (in minutes)

Here's what you need to do:

1. First, carefully read the consigne:
<consigne>
{consigne}
</consigne>

2. Next, examine the Python code:
<code_python>
{code}
</code_python>

3. Take note of the time spent coding in minutes:
<temps_code>{temps_code}</temps_code>

4. Analyze the code for the following aspects:
   - Correctness: Does it fulfill the requirements specified in the consigne?
   - Efficiency: Is the code optimized and well-structured?
   - Readability: Is the code easy to understand and well-commented?
   - Complexity: Is the solution appropriate for the given time frame?
   - Difficulty: was the code complex to implement or not?

5. Consider the time spent coding in relation to the code quality and complexity.

6. In <reasoning> tags, provide a detailed explanation of your evaluation, addressing the points mentioned above and justifying your score.

7. Based on your analysis, assign an XP score out of 500 points. Place this score in <score> tags at the end of your response.

Remember to be fair and consistent in your evaluation. Your reasoning should clearly support the score you assign."""
            }
        ],
        "model": "llama-3.1-8b-instant",
        "temperature": 0.8,
        "max_tokens": 2000,
        "top_p": 1,
        "stream": False,
        "stop": None
    }

    response = requests.post(url, headers=headers, json=payload)

    try:
        response.raise_for_status()
        content = response.json()
    except requests.exceptions.HTTPError as http_err:
        return jsonify({"error": f"HTTP error occurred: {http_err}"}), response.status_code
    except requests.exceptions.RequestException as req_err:
        return jsonify({"error": f"Request error occurred: {req_err}"}), 500
    except json.JSONDecodeError:
        return jsonify({"error": "Invalid JSON response from API"}), 500

    try:
        message_content = content['choices'][0]['message']['content']
        score = int(message_content.split("<score>")[1].split("</score>")[0].strip())

        conn = sqlite3.connect('scores.db')
        cursor = conn.cursor()
        cursor.execute('SELECT total_score FROM users WHERE username = ?', (username,))
        row = cursor.fetchone()

        if row:
            total_score = row[0] + score
            cursor.execute('UPDATE users SET total_score = ? WHERE username = ?', (total_score, username))
        else:
            total_score = score
            cursor.execute('INSERT INTO users (username, total_score) VALUES (?, ?)', (username, total_score))

        conn.commit()
        conn.close()

        return jsonify({"score": score, "total_score": total_score})
    except (KeyError, IndexError, ValueError) as e:
        return jsonify({"error": f"Error parsing API response: {e}"}), 500
    

@app.route('/scores', methods=['GET'])
def get_scores():
    conn = sqlite3.connect('scores.db')
    cursor = conn.cursor()
    cursor.execute('SELECT username, total_score, image_url FROM users ORDER BY total_score DESC')
    rows = cursor.fetchall()
    conn.close()

    scores = [{"username": row[0], "total_score": row[1], "image_url": row[2]} for row in rows]
    return jsonify(scores)


@app.route('/add_profile_image', methods=['POST'])
def add_profile_image():
    data = request.json
    username = data.get('username')
    image_url = data.get('image_url')

    if not username or not image_url:
        return jsonify({"error": "Missing username or image_url"}), 400

    conn = sqlite3.connect('scores.db')
    cursor = conn.cursor()
    cursor.execute('SELECT * FROM users WHERE username = ?', (username,))
    row = cursor.fetchone()

    if not row:
        cursor.execute('INSERT INTO users (username, total_score, image_url) VALUES (?, ?, ?)', (username, 0, image_url))
    else:
        cursor.execute('UPDATE users SET image_url = ? WHERE username = ?', (image_url, username))

    conn.commit()
    conn.close()

    return jsonify({"message": "Profile image updated successfully"}), 200

@app.route('/user/<username>', methods=['GET'])
def get_user(username):
    conn = sqlite3.connect('scores.db')
    cursor = conn.cursor()
    cursor.execute('SELECT username, total_score, image_url FROM users WHERE username = ?', (username,))
    row = cursor.fetchone()
    conn.close()

    if not row:
        return jsonify({"error": "User not found"}), 404

    user_info = {
        "username": row[0],
        "total_score": row[1],
        "image_url": row[2]
    }

    return jsonify(user_info), 200


if __name__ == '__main__':
    app.run(debug=True, host="0.0.0.0")
